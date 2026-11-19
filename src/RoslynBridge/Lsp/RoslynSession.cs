// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Lsp;

/// <summary>
/// Gives the tools the current Roslyn connection for the lifetime of the MCP host. Document and workspace
/// file calls may run concurrently. Callers must serialize start, stop and reload.
/// </summary>
[DebuggerDisplay("running = {IsRunning}, loading = {IsLoading}")]
internal sealed partial class RoslynSession(
    SettingsMonitor settings,
    WorkspacePaths paths,
    RoslynLauncher launcher,
    ILogger<RoslynSession> logger
) : IAsyncDisposable
{
    private RoslynConnection? _connection;
    private CancellationTokenSource? _startupCancellation;
    private Task<Result>? _startup;
    private bool _disposed;

    public JsonObject? Capabilities => _connection?.Capabilities;

    public bool IsRunning => _connection is { IsAlive: true };

    public bool IsLoading => _startup is { IsCompleted: false };

    public bool IsReady => IsRunning && _startup is { IsCompletedSuccessfully: true };

    /// <summary>
    /// The latest unfinished progress while the workspace loads, otherwise null.
    /// </summary>
    public LoadProgress? Progress => IsLoading ? _connection?.Progress : null;

    public Func<
        JsonObject,
        CancellationToken,
        Task<ApplyWorkspaceEditResult>
    >? ApplyWorkspaceEditAsync { get; private set; }

    private RoslynConnection Connection =>
        _connection ?? throw new InvalidOperationException("The Roslyn session is not started.");

    public void RequireReload() => _connection?.RequireRestart();

    /// <summary>
    /// Routes workspace/applyEdit requests to <paramref name="handler"/> until the result is disposed.
    /// Without a handler, the bridge refuses them.
    /// </summary>
    public IDisposable HandleWorkspaceEdits(
        Func<JsonObject, CancellationToken, Task<ApplyWorkspaceEditResult>> handler
    )
    {
        ApplyWorkspaceEditAsync = handler;
        return new WorkspaceEditScope(this);
    }

    public async Task StartLoadingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReady || IsLoading)
        {
            return;
        }

        await StopConnectionAsync();
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
        // A load started at launch has no caller until the first tool call
        _ = _startup.ContinueWith(
            LogLoadFailure,
            CancellationToken.None,
            TaskContinuationOptions.NotOnCanceled,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// The load continues when the startup timeout ends the wait.
    /// </summary>
    public async Task<Result> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await StartLoadingAsync();
        var startup = _startup!;
        try
        {
            return await startup.WaitAsync(settings.CurrentValue.StartupTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Load may finish between the timeout and this check
            return startup.IsCompleted
                ? await startup
                : new ToolError(ErrorCodes.Loading, LoadingMessage(Progress));
        }
    }

    public async Task<Result> ReloadAsync(CancellationToken cancellationToken)
    {
        await StopAsync();
        return await EnsureStartedAsync(cancellationToken);
    }

    public Task<Result<JsonNode?>> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken
    ) => Connection.RequestAsync(method, parameters, cancellationToken);

    public Task<Dictionary<string, int>> CopyDocumentVersionsAsync(
        CancellationToken cancellationToken
    ) => Connection.CopyDocumentVersionsAsync(cancellationToken);

    public Task<Result<string>> OpenDocumentAsync(
        string file,
        CancellationToken cancellationToken
    ) => Connection.OpenDocumentAsync(file, cancellationToken);

    public Task<Result<List<string>>> OpenDocumentsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    ) => Connection.OpenDocumentsAsync(files, cancellationToken);

    public Task<Result> SynchronizeAsync(CancellationToken cancellationToken) =>
        Connection.SynchronizeAsync(cancellationToken);

    public Task<Result> SynchronizeFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    ) => Connection.SynchronizeFilesAsync(files, cancellationToken);

    public Task<Result> SynchronizeNewFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    ) => Connection.SynchronizeNewFilesAsync(files, cancellationToken);

    public Task<Result<Dictionary<string, FileStamp>>> SynchronizeWorkspaceAsync(
        CancellationToken cancellationToken
    ) => Connection.SynchronizeWorkspaceAsync(cancellationToken);

    public Task<Result<string[]>> WorkspaceFilesAsync(CancellationToken cancellationToken) =>
        Connection.WorkspaceFilesAsync(cancellationToken);

    public Task<Result<LspDiagnosticReport>> DiagnosticReportAsync(
        string uri,
        CancellationToken cancellationToken
    ) => Connection.DiagnosticReportAsync(uri, cancellationToken);

    public Task<Result<ProjectContext[]>> ProjectContextsAsync(
        string uri,
        CancellationToken cancellationToken
    ) => Connection.ProjectContextsAsync(uri, cancellationToken);

    public Task<Result<HashSet<string>>> WaitForProjectsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    ) => Connection.WaitForProjectsAsync(files, cancellationToken);

    public Task<Result<ExtensionReferences>> FindReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) => Connection.FindReferencesAsync(parameters, cancellationToken);

    public Task<Result<ExtensionCallers>> FindCallersAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) => Connection.FindCallersAsync(parameters, cancellationToken);

    public Task<Result<JsonNode>> FindUnusedAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    ) => Connection.FindUnusedAsync(files, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _startupCancellation?.Dispose();
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The session disposes the connection when it stops."
    )]
    private async Task<Result> StartAsync(CancellationToken cancellationToken)
    {
        var options = settings.CurrentValue;
        IRoslynServer server;
        try
        {
            server = launcher(options, paths);
        }
        catch (Exception exception) when (exception is Win32Exception || ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }

        _connection = new RoslynConnection(
            server,
            options,
            settings,
            paths,
            new ClientCallbacks(this, paths, logger),
            logger
        );
        Result started;
        try
        {
            started = await _connection.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            await StopConnectionAsync();
            return ToolError.Io(exception.Message);
        }
        catch
        {
            await StopConnectionAsync();
            throw;
        }

        if (started is ToolError)
        {
            await StopConnectionAsync();
        }

        return started;
    }

    private static string LoadingMessage(LoadProgress? progress)
    {
        const string Loading = "Roslyn is still loading the workspace.";
        const string Advice = "Retry the call, or use lsp status to follow the load.";
        return progress is null
            ? $"{Loading} {Advice}"
            : $"{Loading} Progress: {progress.Text}. {Advice}";
    }

    private void LogLoadFailure(Task<Result> load)
    {
        if (load.IsFaulted)
        {
            LogLoadCrashed(logger, load.Exception.GetBaseException());
        }
        else if (load.Result is ToolError error)
        {
            LogLoadFailed(logger, error.Message);
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

        await StopConnectionAsync();
    }

    private async Task StopConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped Roslyn while it was loading.")]
    private static partial void LogStartupStopped(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Roslyn failed to load the workspace: {Reason}"
    )]
    private static partial void LogLoadFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Roslyn failed to load the workspace.")]
    private static partial void LogLoadCrashed(ILogger logger, Exception exception);

    private sealed class WorkspaceEditScope(RoslynSession session) : IDisposable
    {
        public void Dispose() => session.ApplyWorkspaceEditAsync = null;
    }
}
