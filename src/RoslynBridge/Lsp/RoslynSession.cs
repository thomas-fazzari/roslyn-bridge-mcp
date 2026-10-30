// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// Owns one Roslyn process and its workspace for the lifetime of the MCP host.
/// </summary>
/// <remarks>
/// Document and workspace file calls may run concurrently, and callers must serialize start, stop and reload.
/// </remarks>
[DebuggerDisplay("running = {IsRunning}, open documents = {_documents.Count}")]
internal sealed partial class RoslynSession(
    SettingsMonitor settings,
    WorkspacePaths paths,
    RoslynLauncher launcher,
    ILogger<RoslynSession> logger
) : IAsyncDisposable
{
    internal const int MaximumProtocolBytes = 16 * 1024 * 1024;

    private const int StderrBufferCharacters = 2048;
    private const int StderrTailCharacters = 4096;
    private readonly StringBuilder _stderrTail = new();
    private readonly Lock _stderrLock = new();

    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(3);
    private IRoslynServer? _server;
    private JsonRpc? _rpc;
    private Task? _stderrTask;
    private CancellationTokenSource? _processLifetime;
    private CancellationTokenSource? _startupCancellation;
    private Task? _startup;
    private TaskCompletionSource _initialized = NewCompletionSource();

    private bool _disposed;
    private bool _restartRequired;
    private BridgeOptions _launched = new();

    public JsonObject? Capabilities { get; private set; }

    public bool IsRunning =>
        !_restartRequired
        && _server is { HasExited: false }
        && _rpc is { IsDisposed: false, Completion.IsCompleted: false };

    public bool IsLoading => _startup is { IsCompleted: false };

    public bool IsReady => IsRunning && _startup is { IsCompletedSuccessfully: true };

    public void RequireReload() => _restartRequired = true;

    public Func<
        JsonObject,
        CancellationToken,
        Task<ApplyWorkspaceEditResult>
    >? ApplyWorkspaceEditAsync { get; private set; }

    /// <summary>
    /// Sends Roslyn's workspace/applyEdit requests to <paramref name="handler"/> until the result is disposed.
    /// Without a handler, the bridge refuses them.
    /// </summary>
    public IDisposable HandleWorkspaceEdits(
        Func<JsonObject, CancellationToken, Task<ApplyWorkspaceEditResult>> handler
    )
    {
        ApplyWorkspaceEditAsync = handler;
        return new WorkspaceEditScope(this);
    }

    /// <summary>
    /// Starts loading the workspace unless Roslyn is ready or loading, without waiting for the load.
    /// </summary>
    public async Task StartLoadingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady || IsLoading)
        {
            return;
        }

        await StopServerAsync();
        _startupCancellation?.Dispose();
        _startupCancellation = new CancellationTokenSource();
        var cancellationToken = _startupCancellation.Token;
        _startup = Task.Run(
            () =>
                BridgeTelemetry.TraceAsync(
                    "roslyn_bridge.workspace.load",
                    BridgeTelemetry.LoadDuration,
                    () => StartAsync(cancellationToken)
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Starts Roslyn when needed and waits up to the startup timeout for the workspace to load.
    /// </summary>
    /// <remarks>
    /// The load continues when the wait ends, so a later call can use it.
    /// </remarks>
    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await StartLoadingAsync();
        try
        {
            await _startup!.WaitAsync(settings.CurrentValue.StartupTimeout, cancellationToken);
        }
        catch (TimeoutException) when (IsLoading)
        {
            throw new WorkspaceLoadingException(Progress);
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            StartServer();
            await InitializeAsync(cancellationToken);
        }
        catch
        {
            await StopServerAsync();
            throw;
        }
    }

    public Task<JsonNode?> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    ) =>
        BridgeTelemetry.TraceLspAsync(
            method,
            () => InvokeAsync(method, parameters, cancellationToken)
        );

    private async Task<JsonNode?> InvokeAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    )
    {
        var rpc = _rpc ?? throw new InvalidOperationException("The Roslyn session is not started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.CurrentValue.RequestTimeout);
        try
        {
            return await rpc.InvokeWithParameterObjectAsync<JsonNode>(
                method,
                parameters,
                timeout.Token
            );
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Roslyn request '{method}' exceeded the request timeout.",
                exception
            );
        }
        catch (Exception exception) when (exception is ConnectionLostException or IOException)
        {
            // A write to a dead process fails with a broken pipe before the connection loss is noticed
            _restartRequired = true;
            throw new IOException(
                await WithStderrAsync($"Roslyn exited during '{method}'.", cancellationToken),
                exception
            );
        }
    }

    private async Task<string> WithStderrAsync(string message, CancellationToken cancellationToken)
    {
        if (_stderrTask is { } stderr)
        {
            // The pipe closes when Roslyn exits, so its last lines arrive shortly after
            try
            {
                await stderr.WaitAsync(_shutdownTimeout, cancellationToken);
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

    public async Task NotifyAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    )
    {
        var rpc = _rpc ?? throw new InvalidOperationException("The Roslyn session is not started.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await rpc.NotifyWithParameterObjectAsync(method, parameters)
                .WaitAsync(cancellationToken);
        }
        catch
        {
            // A canceled write may still reach Roslyn, so document versions are untrusted until a reload
            _restartRequired = true;
            throw;
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await StopAsync();
        await EnsureStartedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _startupCancellation?.Dispose();
        _documentLock.Dispose();
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void StartServer()
    {
        _restartRequired = false;
        _launched = settings.CurrentValue;
        _server = launcher(_launched, paths);
        _processLifetime = new CancellationTokenSource();

        lock (_stderrLock)
        {
            _stderrTail.Clear();
        }

        // Roslyn's callbacks belong to no tool call, so the listeners must not capture the load activity
        var activity = Activity.Current;
        Activity.Current = null;
        try
        {
            _stderrTask = ReadStderrAsync(_server.Error, _processLifetime.Token);
            StartRpc(_server);
            StartWatching();
        }
        finally
        {
            Activity.Current = activity;
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "JsonRpc owns and disposes the message handler, which disposes its formatter."
    )]
    private void StartRpc(IRoslynServer server)
    {
        var reader = new BoundedPipeReader(PipeReader.Create(server.Output), MaximumProtocolBytes);
        var writer = PipeWriter.Create(server.Input);
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = LspJson.Options };
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(writer, reader, formatter));
        _rpc.AddLocalRpcTarget(new ClientCallbacks(this, paths, logger, _initialized));
        _rpc.StartListening();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var rpc =
            _rpc ?? throw new InvalidOperationException("The Roslyn transport is not started.");

        var initialize = ClientCapabilities.Create(paths);
        JsonObject? result;
        try
        {
            result = await BridgeTelemetry.TraceLspAsync(
                LspMethods.Initialize,
                () =>
                    rpc.InvokeWithParameterObjectAsync<JsonObject>(
                        LspMethods.Initialize,
                        initialize,
                        cancellationToken
                    )
            );
        }
        catch (Exception exception) when (exception is ConnectionLostException or IOException)
        {
            throw new IOException(
                await WithStderrAsync("Roslyn exited during initialization.", cancellationToken),
                exception
            );
        }

        Capabilities = result?["capabilities"]?.AsObject();

        await NotifyAsync(LspMethods.Initialized, new JsonObject(), cancellationToken);
        await OpenConfiguredProjectsAsync(cancellationToken);

        var initializationOrDisconnect = Task.WhenAny(_initialized.Task, rpc.Completion);

        var completed = await initializationOrDisconnect.WaitAsync(cancellationToken);
        await completed;
        if (completed != _initialized.Task)
        {
            throw new IOException(
                await WithStderrAsync(
                    "Roslyn disconnected while loading the workspace.",
                    cancellationToken
                )
            );
        }

        _workspaceFiles = ScanWorkspace(cancellationToken);
        LogWorkspaceLoaded(logger, _workspaceFiles.Count);
    }

    // Roslyn finds what to load itself unless the settings name a solution or projects
    private async Task OpenConfiguredProjectsAsync(CancellationToken cancellationToken)
    {
        if (_launched.Solution is { } solution)
        {
            await NotifyAsync(
                LspMethods.SolutionOpen,
                new JsonObject { ["solution"] = new Uri(solution).AbsoluteUri },
                cancellationToken
            );
        }
        else if (_launched.Projects.Count > 0)
        {
            await NotifyAsync(
                LspMethods.ProjectOpen,
                new JsonObject
                {
                    ["projects"] = new JsonArray([
                        .. _launched.Projects.Select(project =>
                            (JsonNode)new Uri(project).AbsoluteUri
                        ),
                    ]),
                },
                cancellationToken
            );
        }
    }

    private async Task StopAsync()
    {
        if (_startup is { IsCompleted: false } startup)
        {
            await _startupCancellation!.CancelAsync();
            try
            {
                await startup;
            }
            catch (Exception exception)
            {
                LogStartupStopped(logger, exception);
            }
        }

        await StopServerAsync();
    }

    private async Task StopServerAsync()
    {
        var server = _server;
        var rpc = _rpc;
        _rpc = null;
        _server = null;

        ClearWorkspace();

        if (server is null)
        {
            return;
        }

        try
        {
            await ShutdownAsync(rpc, server);
        }
        finally
        {
            await DisposeProcessResourcesAsync(rpc, server);
        }
    }

    private void ClearWorkspace()
    {
        Capabilities = null;
        _initialized = NewCompletionSource();
        _documents.Clear();
        _openedOrder.Clear();
        _workspaceFiles.Clear();
        _createdFiles.Clear();
        _watchRegistrations.Clear();
        _watcher?.Dispose();
        _watcher = null;
        _pendingFiles.Clear();
        _scanRequested = 0;
    }

    private async Task DisposeProcessResourcesAsync(JsonRpc? rpc, IRoslynServer server)
    {
        rpc?.Dispose();
        ClearProgress();
        ClearDiagnosticReports();
        if (rpc is not null)
        {
            // JsonRpc disposes its message handler in the background, then completes
            await rpc.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_processLifetime is not null)
        {
            await _processLifetime.CancelAsync();
            _processLifetime.Dispose();
            _processLifetime = null;
        }

        if (_stderrTask is not null)
        {
            await _stderrTask;
            _stderrTask = null;
        }

        server.Dispose();
    }

    private async Task ShutdownAsync(JsonRpc? rpc, IRoslynServer server)
    {
        if (server.HasExited)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_shutdownTimeout);
        try
        {
            if (rpc is { IsDisposed: false })
            {
                await rpc.InvokeWithCancellationAsync(
                    LspMethods.Shutdown,
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

        if (!server.HasExited)
        {
            server.Kill();
        }

        await server.WaitForExitAsync(CancellationToken.None);
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[StderrBufferCharacters];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    return;
                }

                var message = new string(buffer, 0, count);
                lock (_stderrLock)
                {
                    _stderrTail.Append(message);
                    if (_stderrTail.Length > StderrTailCharacters)
                    {
                        _stderrTail.Remove(0, _stderrTail.Length - StderrTailCharacters);
                    }
                }

                if (logger.IsEnabled(LogLevel.Debug))
                {
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped Roslyn while it was loading.")]
    private static partial void LogStartupStopped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn stderr: {Message}")]
    private static partial void LogStderr(ILogger logger, string message);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Roslyn loaded the workspace, with {FileCount} watched files."
    )]
    private static partial void LogWorkspaceLoaded(ILogger logger, int fileCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn stderr stream closed.")]
    private static partial void LogStderrClosed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipped {Path}: the document exceeds the one million UTF-16 code unit limit."
    )]
    private static partial void LogOversizedDocument(ILogger logger, string path);

    private sealed class WorkspaceEditScope(RoslynSession session) : IDisposable
    {
        public void Dispose() => session.ApplyWorkspaceEditAsync = null;
    }
}
