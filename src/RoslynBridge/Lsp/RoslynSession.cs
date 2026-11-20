// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;
using static RoslynBridge.Lsp.SessionState;

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
    private SessionState _state = new Stopped();

    public JsonObject? Capabilities => CurrentConnection?.Capabilities;

    /// <summary>
    /// Changes each time a Roslyn connection stops, so state tied to one Roslyn process can tell it is stale.
    /// </summary>
    public int Generation { get; private set; }

    public bool IsRunning => CurrentConnection?.IsAlive is true;

    public bool IsLoading => _state is Launching or Initializing;

    public bool IsReady => _state is Ready { Connection.IsAlive: true };

    /// <summary>
    /// The latest unfinished progress while the workspace loads, otherwise null.
    /// </summary>
    public LoadProgress? Progress =>
        _state switch
        {
            Initializing { Connection.Progress: var progress } => progress,
            Stopped or Launching or Ready or Failed or Disposed => null,
        };

    private RoslynConnection Connection =>
        CurrentConnection
        ?? throw new InvalidOperationException("The Roslyn session is not started.");

    /// <summary>
    /// Restarts Roslyn before the next request, unless a connection newer than <paramref name="generation"/> already reads the files from disk.
    /// </summary>
    public void RequireReload(int generation)
    {
        if (generation == Generation)
            CurrentConnection?.RequireRestart();
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The session disposes the cancellation source when it stops the load."
    )]
    public async Task StartLoadingAsync()
    {
        ObjectDisposedException.ThrowIf(_state is Disposed, this);
        if (IsReady || IsLoading)
            return;

        await StopConnectionAsync();
        var cancellation = new CancellationTokenSource();
        // The load updates the state, so it waits until the state points to this load
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var startup = Task.Run(
            async () =>
            {
                await published.Task;
                try
                {
                    return await BridgeTelemetry.TraceAsync(
                        "roslyn_bridge.workspace.load",
                        BridgeTelemetry.LoadDuration,
                        () => StartAsync(cancellation)
                    );
                }
                finally
                {
                    // Every load that leaves no ready connection ends here (crash included)
                    Advance(cancellation, static load => new Failed(load));
                }
            },
            cancellation.Token
        );
        _state = new Launching(new SessionLoad(startup, cancellation));
        published.SetResult();
        // A load started at launch has no caller until the first tool call
        _ = startup.ContinueWith(
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
        var state = _state;
        if (state is Ready)
            return Result.Success;

        var startup = LoadOf(state)?.Startup
            ?? throw new InvalidOperationException("The Roslyn session is not started.");
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
        DocumentPosition position,
        CancellationToken cancellationToken
    ) => Connection.FindReferencesAsync(position, cancellationToken);

    public Task<Result<ExtensionCallers>> FindCallersAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) => Connection.FindCallersAsync(position, cancellationToken);

    /// <summary>
    /// Routes the workspace/applyEdit requests of the command to <paramref name="onEdit"/>.
    /// </summary>
    public Task<Result<JsonNode?>> ExecuteCommandAsync(
        LspCommand command,
        WorkspaceEditHandler onEdit,
        CancellationToken cancellationToken
    ) => Connection.ExecuteCommandAsync(command, onEdit, cancellationToken);

    public Task<Result<UnusedReport>> FindUnusedAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    ) => Connection.FindUnusedAsync(files, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_state is Disposed)
            return;

        try
        {
            await StopAsync();
        }
        finally
        {
            _state = new Disposed();
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The session disposes the connection when it stops."
    )]
    private async Task<Result> StartAsync(CancellationTokenSource cancellation)
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

        var connection = new RoslynConnection(
            server,
            options,
            paths,
            new ClientCallbacks(paths, logger),
            logger
        );
        Advance(cancellation, load => new Initializing(load, connection));
        Result started;
        try
        {
            started = await connection.StartAsync(cancellation.Token);
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            await AbandonAsync(cancellation, connection);
            return ToolError.Io(exception.Message);
        }
        catch
        {
            await AbandonAsync(cancellation, connection);
            throw;
        }

        if (started is ToolError)
            await AbandonAsync(cancellation, connection);
        else
            Advance(cancellation, _ => new Ready(connection));

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

    private RoslynConnection? CurrentConnection =>
        _state switch
        {
            Initializing { Connection: var connection } => connection,
            Ready { Connection: var connection } => connection,
            Stopped or Launching or Failed or Disposed => null,
        };

    private static SessionLoad? LoadOf(SessionState state) =>
        state switch
        {
            Launching { Load: var load } => load,
            Initializing { Load: var load } => load,
            Failed { Load: var load } => load,
            Stopped or Ready or Disposed => null,
        };

    // Ignores a load that has failed, or that Stop or Reload has replaced since it began
    private void Advance(CancellationTokenSource cancellation, Func<SessionLoad, SessionState> next)
    {
        var state = _state;
        if (state is not (Launching or Initializing) || LoadOf(state) is not { } load)
            return;

        if (load.Cancellation == cancellation)
            _state = next(load);
    }

    // The load stays active until the connection is disposed, so Stop waits for the Roslyn process to exit
    private async Task AbandonAsync(
        CancellationTokenSource cancellation,
        RoslynConnection connection
    )
    {
        Advance(cancellation, static load => new Launching(load));
        await DisposeConnectionAsync(connection);
    }

    private async Task StopAsync()
    {
        var state = _state;
        if (state is Launching or Initializing && LoadOf(state) is { } load)
        {
            await load.Cancellation.CancelAsync();
            try
            {
                await load.Startup;
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
        var connection = CurrentConnection;
        LoadOf(_state)?.Cancellation.Dispose();

        _state = new Stopped();
        if (connection is not null)
            await DisposeConnectionAsync(connection);
    }

    private async Task DisposeConnectionAsync(RoslynConnection connection)
    {
        Generation++;
        await connection.DisposeAsync();
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
}

internal closed record SessionState
{
    internal sealed record Stopped : SessionState;

    /// <summary>
    /// The load has no connection. It starts Roslyn, or stops a connection that failed to load.
    /// </summary>
    internal sealed record Launching(SessionLoad Load) : SessionState;

    /// <summary>
    /// Roslyn runs and loads the workspace.
    /// </summary>
    internal sealed record Initializing(SessionLoad Load, RoslynConnection Connection)
        : SessionState;

    internal sealed record Ready(RoslynConnection Connection) : SessionState;

    /// <summary>
    /// The load ended without a ready connection. The next call starts a new load.
    /// </summary>
    internal sealed record Failed(SessionLoad Load) : SessionState;

    internal sealed record Disposed : SessionState;
}

/// <param name="Cancellation">Stops the load. The session disposes it when it stops a load that did not reach Ready.</param>
internal sealed record SessionLoad(Task<Result> Startup, CancellationTokenSource Cancellation);
