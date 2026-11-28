// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// One Roslyn process and the state the bridge keeps for it.
/// A restart creates a new connection.
/// </summary>
[DebuggerDisplay("alive = {IsAlive}, open documents = {_documents.Count}")]
internal sealed partial class RoslynConnection(
    IRoslynServer server,
    BridgeOptions launched,
    WorkspacePaths paths,
    ClientCallbacks callbacks,
    TimeProvider time,
    ILogger logger
) : IAsyncDisposable
{
    internal const int MAXIMUM_PROTOCOL_BYTES = 16 * 1024 * 1024;

    private const int STDERR_BUFFER_CHARACTERS = 2048;
    private const int STDERR_TAIL_CHARACTERS = 4096;
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly StringBuilder _stderrTail = new();
    private readonly Lock _stderrLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private JsonRpc? _rpc;
    private Task? _stderrTask;

    public JsonObject? Capabilities { get; private set; }

    public bool IsAlive
    {
        get =>
            !field
            && !server.HasExited
            && _rpc is { IsDisposed: false, Completion.IsCompleted: false };
        private set;
    }

    public LoadProgress? Progress => callbacks.Progress;

    public ImmutableArray<ProjectIssue> ProjectIssues => callbacks.ProjectIssues;

    public void RequireRestart() => IsAlive = true;

    public Task<Result> StartAsync(CancellationToken cancellationToken)
    {
        // Roslyn's callbacks belong to no tool call, so the listeners must not capture the load activity
        var activity = Activity.Current;
        Activity.Current = null;
        try
        {
            _stderrTask = ReadStderrAsync(server.Error, _lifetime.Token);
            StartRpc();
            StartWatching();
        }
        finally
        {
            Activity.Current = activity;
        }

        return InitializeAsync(cancellationToken);
    }

    /// <summary>
    /// A null value is a valid answer, as for a position without a symbol.
    /// </summary>
    public Task<Result<JsonNode?>> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    ) =>
        BridgeTelemetry.TraceLspAsync(method, () =>
            InvokeAsync(method, parameters, cancellationToken)
        );

    // Roslyn sends the command's workspace/applyEdit requests over the connection that runs it
    public Task<Result<JsonNode?>> ExecuteCommandAsync(
        LspCommand command,
        WorkspaceEditHandler onEdit,
        CancellationToken cancellationToken
    ) =>
        callbacks.RoutingEditsAsync(onEdit, () =>
            RequestAsync(LspMethods.WORKSPACE_EXECUTE_COMMAND, command.Raw, cancellationToken)
        );

    public async Task<Result> NotifyAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    )
    {
        var rpc = Rpc;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await rpc.NotifyWithParameterObjectAsync(method, parameters)
                .WaitAsync(cancellationToken);
            return Result.Success;
        }
        catch (Exception exception) when (exception is ConnectionLostException or IOException)
        {
            IsAlive = true;
            return ToolError.Io(
                await WithStderrAsync($"Roslyn exited during '{method}'.", cancellationToken)
            );
        }
        catch (OperationCanceledException)
        {
            // A canceled write may still reach Roslyn, so document versions are untrusted until a reload
            IsAlive = true;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            await DisposeProcessResourcesAsync();
        }
    }

    private JsonRpc Rpc =>
        _rpc ?? throw new InvalidOperationException("The Roslyn connection is not started.");

    private async Task<Result<JsonNode?>> InvokeAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    )
    {
        var rpc = Rpc;
        using var deadline = new CancellationTokenSource(launched.RequestTimeout, time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );
        try
        {
            return await rpc.InvokeWithParameterObjectAsync<JsonNode?>(
                method,
                parameters,
                timeout.Token
            );
        }
        // StreamJsonRpc turns a RequestCancelled answer into a cancellation of the call
        catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
        {
            return new ToolError(
                ErrorCodes.LSP,
                $"Roslyn cancelled request '{method}'.",
                RpcCode: REQUEST_CANCELLED_CODE
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolError(
                ErrorCodes.TIMEOUT,
                $"Roslyn request '{method}' exceeded the request timeout."
            );
        }
        catch (Exception exception) when (exception is ConnectionLostException or IOException)
        {
            // A write to a dead process fails with a broken pipe before the connection loss is noticed
            IsAlive = true;
            return ToolError.Io(
                await WithStderrAsync($"Roslyn exited during '{method}'.", cancellationToken)
            );
        }
        catch (Exception exception) when (exception is RemoteRpcException or JsonException)
        {
            return ProtocolError(exception);
        }
    }

    // A connection loss is also a RemoteRpcException, callers catch it first
    private static ToolError ProtocolError(Exception exception) =>
        exception is RemoteInvocationException remote
            ? new ToolError(ErrorCodes.LSP, remote.Message, RpcCode: remote.ErrorCode)
            : ToolError.InvalidLspData(exception.Message);

    private async Task<string> WithStderrAsync(string message, CancellationToken cancellationToken)
    {
        if (_stderrTask is { } stderr)
        {
            // The pipe closes when Roslyn exits, so its last lines arrive shortly after
            try
            {
                await stderr.WaitAsync(_shutdownTimeout, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Report what arrived so far
            }
        }

        string tail;
        lock (_stderrLock)
        {
            tail = _stderrTail.ToString().Trim();
        }

        return tail.Length == 0 ? message : $"{message} Roslyn stderr: {tail}";
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "JsonRpc owns and disposes the message handler, which disposes its formatter."
    )]
    private void StartRpc()
    {
        var reader = new BoundedPipeReader(
            PipeReader.Create(server.Output),
            MAXIMUM_PROTOCOL_BYTES
        );
        var writer = PipeWriter.Create(server.Input);
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = LspJson.Options };
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(writer, reader, formatter));
        _rpc.AddLocalRpcTarget(callbacks);
        _rpc.StartListening();
    }

    private async Task<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        var rpc = Rpc;
        var initialize = ClientCapabilities.Create(paths);
        var initialized = await BridgeTelemetry.TraceLspAsync<JsonObject?>(
            LspMethods.INITIALIZE,
            async () =>
            {
                try
                {
                    return await rpc.InvokeWithParameterObjectAsync<JsonObject?>(
                        LspMethods.INITIALIZE,
                        initialize,
                        cancellationToken
                    );
                }
                catch (Exception exception)
                    when (exception is ConnectionLostException or IOException)
                {
                    return ToolError.Io(
                        await WithStderrAsync(
                            "Roslyn exited during initialization.",
                            cancellationToken
                        )
                    );
                }
                catch (Exception exception) when (exception is RemoteRpcException or JsonException)
                {
                    return ProtocolError(exception);
                }
            }
        );
        if (initialized.IsError(out var error, out var result))
            return error;

        Capabilities = result?["capabilities"]?.AsObject();

        if (
            await NotifyAsync(LspMethods.INITIALIZED, new JsonObject(), cancellationToken)
            is ToolError notifyError
        )
        {
            return notifyError;
        }

        if (await OpenConfiguredProjectsAsync(cancellationToken) is ToolError openError)
            return openError;

        // A message that does not parse faults the completion, which is a disconnect too
        var completed = await Task.WhenAny(callbacks.Initialized, rpc.Completion)
            .WaitAsync(cancellationToken);
        if (completed != callbacks.Initialized)
        {
            var reason = rpc.Completion.Exception?.GetBaseException().Message;
            return ToolError.Io(
                await WithStderrAsync(
                    $"Roslyn disconnected while loading the workspace. {reason}".TrimEnd(),
                    cancellationToken
                )
            );
        }

        await completed;

        if (ScanWorkspace(cancellationToken).IsError(out var scanError, out var files))
            return scanError;

        _workspaceFiles = files;
        if (await ActivateExtensionAsync(cancellationToken) is ToolError activationError)
            return activationError;

        LogWorkspaceLoaded(logger, _workspaceFiles.Count);
        return Result.Success;
    }

    private async Task<Result> OpenConfiguredProjectsAsync(CancellationToken cancellationToken)
    {
        if (launched.Solution is { } solution)
        {
            return await NotifyAsync(
                LspMethods.SOLUTION_OPEN,
                new JsonObject { ["solution"] = WorkspacePaths.FileUri(solution) },
                cancellationToken
            );
        }

        return launched.Projects.Count > 0
            ? await NotifyAsync(
                LspMethods.PROJECT_OPEN,
                new JsonObject
                {
                    ["projects"] = new JsonArray([
                        .. launched.Projects.Select(project =>
                            (JsonNode)WorkspacePaths.FileUri(project)
                        ),
                    ]),
                },
                cancellationToken
            )
            : Result.Success;
    }

    private async Task DisposeProcessResourcesAsync()
    {
        _rpc?.Dispose();
        if (_rpc is not null)
        {
            // JsonRpc disposes its message handler in the background, then completes
            await _rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await _lifetime.CancelAsync();
        if (_stderrTask is not null)
            await _stderrTask;

        server.Dispose();
        _lifetime.Dispose();
        _documentLock.Dispose();
    }

    private async Task ShutdownAsync()
    {
        if (server.HasExited)
            return;

        using var timeout = new CancellationTokenSource(_shutdownTimeout, time);
        try
        {
            if (_rpc is { IsDisposed: false } rpc)
            {
                await rpc.InvokeWithCancellationAsync(
                    LspMethods.SHUTDOWN,
                    cancellationToken: timeout.Token
                );
            }
        }
        catch (Exception exception)
            when (exception
                    is OperationCanceledException
                        or IOException
                        or ConnectionLostException
                        or RemoteInvocationException
                        or ObjectDisposedException
            )
        {
            LogShutdownFailure(logger, exception);
        }

        try
        {
            if (!server.HasExited)
                server.Kill();
        }
        // Killing the tree fails when a child process cannot be stopped
        catch (Exception exception)
            when (exception
                    is AggregateException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception
            )
        {
            LogShutdownFailure(logger, exception);
        }

        await server.WaitForExitAsync(CancellationToken.None);
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[STDERR_BUFFER_CHARACTERS];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    return;

                lock (_stderrLock)
                {
                    _stderrTail.Append(buffer, 0, count);
                    if (_stderrTail.Length > STDERR_TAIL_CHARACTERS)
                        _stderrTail.Remove(0, _stderrTail.Length - STDERR_TAIL_CHARACTERS);
                }

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var message = new string(buffer, 0, count);
                    LogStderr(logger, message);
                }
            }
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            LogStderrClosed(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn did not shut down cleanly.")]
    private static partial void LogShutdownFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn stderr: {Message}")]
    private static partial void LogStderr(ILogger logger, string message);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Roslyn loaded the workspace, with {FileCount} watched files."
    )]
    private static partial void LogWorkspaceLoaded(ILogger logger, int fileCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The file watcher failed, so every call scans the workspace: {Reason}"
    )]
    private static partial void LogWatcherFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn stderr stream closed.")]
    private static partial void LogStderrClosed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipped {Path}: the document exceeds the one million UTF-16 code unit limit."
    )]
    private static partial void LogOversizedDocument(ILogger logger, string path);
}
