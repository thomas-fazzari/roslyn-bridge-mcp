// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using RoslynBridge.Lsp.Protocol;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// Applies a workspace edit that Roslyn sends while a command runs.
/// </summary>
internal delegate Task<ApplyWorkspaceEditResult> WorkspaceEditHandler(
    Result<WorkspaceEdit> edit,
    CancellationToken cancellationToken
);

/// <summary>
/// Answers the requests of one Roslyn process and keeps the state they report.
/// </summary>
internal sealed partial class ClientCallbacks(WorkspacePaths paths, ILogger logger)
{
    private const int MAXIMUM_LOG_MESSAGE_CHARACTERS = 2048;
    private const string PROGRESS_BEGIN = "begin";
    private const string PROGRESS_REPORT = "report";
    private const string PROGRESS_END = "end";
    private const string ERROR_MARKER = "Error while loading ";
    private const string WARNING_MARKER = "Warning while loading ";
    private const string PATH_END = ": ";
    private const string PROJECT_EXTENSION_SUFFIX = "proj";

    private readonly TaskCompletionSource _initialized = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly ConcurrentDictionary<string, byte> _watchRegistrations = new(
        StringComparer.Ordinal
    );

    // Replaced as a whole, so readers never wait for a notification
    private ImmutableList<TrackedProgress> _progress = [];

    private ImmutableDictionary<LocationFile, ProjectIssue> _projectIssues = [];

    private WorkspaceEditHandler? _applyEdit;

    internal Task Initialized => _initialized.Task;

    internal bool WatchesFiles => !_watchRegistrations.IsEmpty;

    /// <summary>
    /// The latest unfinished progress, otherwise null.
    /// </summary>
    internal LoadProgress? Progress => Volatile.Read(ref _progress).LastOrDefault()?.Progress;

    /// <summary>
    /// The latest load error or warning per project, as of its last load attempt. Roslyn logs no success.
    /// </summary>
    internal ImmutableArray<ProjectIssue> ProjectIssues =>
        [.. Volatile.Read(ref _projectIssues).Values];

    /// <summary>
    /// Routes workspace/applyEdit requests to <paramref name="handler"/> while <paramref name="run"/> runs.
    /// Without a handler, the bridge refuses them.
    /// </summary>
    internal async Task<T> RoutingEditsAsync<T>(WorkspaceEditHandler handler, Func<Task<T>> run)
    {
        _applyEdit = handler;
        try
        {
            return await run();
        }
        finally
        {
            _applyEdit = null;
        }
    }

    [JsonRpcMethod(
        LspMethods.WORKSPACE_CONFIGURATION,
        UseSingleObjectParameterDeserialization = true
    )]
    public static object?[] Configuration(ConfigurationParams parameters) =>
        new object?[parameters.Items.Length];

    [JsonRpcMethod(LspMethods.WORKSPACE_WORKSPACE_FOLDERS)]
    public JsonArray WorkspaceFolders() => ClientCapabilities.WorkspaceFolders(paths);

    [JsonRpcMethod(LspMethods.WORKSPACE_APPLY_EDIT, UseSingleObjectParameterDeserialization = true)]
    public Task<ApplyWorkspaceEditResult> ApplyEditAsync(
        ApplyWorkspaceEditParams parameters,
        CancellationToken cancellationToken
    ) =>
        _applyEdit is { } apply
            ? apply(WorkspaceEdit.Parse(parameters.Edit), cancellationToken)
            : Task.FromResult(
                new ApplyWorkspaceEditResult(
                    Applied: false,
                    "No workspace edit is being applied by the current tool call."
                )
            );

    [JsonRpcMethod(
        LspMethods.CLIENT_REGISTER_CAPABILITY,
        UseSingleObjectParameterDeserialization = true
    )]
    public void RegisterCapability(RegistrationParams parameters)
    {
        foreach (var registration in parameters.Registrations)
        {
            if (
                string.Equals(
                    registration.Method,
                    LspMethods.WORKSPACE_DID_CHANGE_WATCHED_FILES,
                    StringComparison.Ordinal
                )
            )
            {
                _watchRegistrations[registration.Id] = 0;
            }
        }
    }

    [JsonRpcMethod(
        LspMethods.CLIENT_UNREGISTER_CAPABILITY,
        UseSingleObjectParameterDeserialization = true
    )]
    public void UnregisterCapability(UnregistrationParams parameters)
    {
        foreach (var registration in parameters.Unregisterations)
        {
            _watchRegistrations.TryRemove(registration.Id, out _);
        }
    }

    [JsonRpcMethod(LspMethods.WORKSPACE_PROJECT_INITIALIZATION_COMPLETE)]
    public void InitializationComplete() => _initialized.TrySetResult();

    // Roslyn waits for this answer before it reports progress
    [JsonRpcMethod(
        LspMethods.WINDOW_WORK_DONE_PROGRESS_CREATE,
        UseSingleObjectParameterDeserialization = true
    )]
    public static void CreateProgress(JsonObject _) { }

    [JsonRpcMethod(LspMethods.PROGRESS, UseSingleObjectParameterDeserialization = true)]
    public void ReportProgress(ProgressParams parameters)
    {
        var token = parameters.Token.ToString();
        ImmutableInterlocked.Update(ref _progress, tracked =>
            WithProgress(tracked, token, parameters.Value)
        );
    }

    [JsonRpcMethod(LspMethods.WINDOW_LOG_MESSAGE, UseSingleObjectParameterDeserialization = true)]
    public void LogMessage(MessageParams parameters)
    {
        RecordProjectIssue(parameters.Message);
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        var message = parameters.Message;
        var boundedMessage = message
            .AsSpan(0, Math.Min(message.Length, MAXIMUM_LOG_MESSAGE_CHARACTERS))
            .ToString();
        LogRoslynMessage(logger, boundedMessage);
    }

    [JsonRpcMethod(LspMethods.WINDOW_SHOW_MESSAGE, UseSingleObjectParameterDeserialization = true)]
    public void ShowMessage(MessageParams parameters) => LogMessage(parameters);

    // An error stays over a later warning of the same project, otherwise the latest issue wins
    private void RecordProjectIssue(string message)
    {
        if (ParseProjectIssue(message) is not { } issue)
            return;

        ImmutableInterlocked.AddOrUpdate(ref _projectIssues, issue.Project, issue, (_, current) =>
            current.Kind is ProjectIssueKind.Error && issue.Kind is ProjectIssueKind.Warning
                ? current
                : issue
        );
    }

    /// <summary>
    /// Reads "[scope] Error while loading PROJECT: MESSAGE", where Roslyn may add several bracketed groups.
    /// </summary>
    private ProjectIssue? ParseProjectIssue(string message)
    {
        var text = message.AsSpan(0, Math.Min(message.Length, MAXIMUM_LOG_MESSAGE_CHARACTERS));
        var lineEnd = text.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            text = text[..lineEnd];

        while (text.StartsWith('['))
        {
            var groupEnd = text.IndexOf(']');
            if (groupEnd < 0)
                return null;

            text = text[(groupEnd + 1)..].TrimStart();
        }

        ProjectIssueKind kind;
        if (text.StartsWith(ERROR_MARKER, StringComparison.Ordinal))
        {
            kind = ProjectIssueKind.Error;
            text = text[ERROR_MARKER.Length..];
        }
        else if (text.StartsWith(WARNING_MARKER, StringComparison.Ordinal))
        {
            kind = ProjectIssueKind.Warning;
            text = text[WARNING_MARKER.Length..];
        }
        else
        {
            return null;
        }

        var pathEnd = ProjectPathEnd(text);
        if (pathEnd < 0)
            return null;

        return Uri.TryCreate(text[..pathEnd].ToString(), UriKind.Absolute, out var uri)
            ? new ProjectIssue(
                paths.LocationOf(uri.AbsoluteUri),
                kind,
                text[(pathEnd + PATH_END.Length)..].Trim().ToString()
            )
            : null;
    }

    // The path may hold spaces and ": ", so it ends at the first ": " that follows a project extension
    private static int ProjectPathEnd(ReadOnlySpan<char> text)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var index = text[offset..].IndexOf(PATH_END, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            index += offset;
            var extension = Path.GetExtension(text[..index]);
            if (extension.EndsWith(PROJECT_EXTENSION_SUFFIX, StringComparison.OrdinalIgnoreCase))
                return index;

            offset = index + 1;
        }

        return -1;
    }

    /// <summary>
    /// A report for an unknown token leaves the progress unchanged.
    /// </summary>
    private static ImmutableList<TrackedProgress> WithProgress(
        ImmutableList<TrackedProgress> tracked,
        string token,
        WorkDoneProgress value
    )
    {
        var index = tracked.FindIndex(entry =>
            string.Equals(entry.Token, token, StringComparison.Ordinal)
        );
        var percentage = value.Percentage;
        switch (value.Kind)
        {
            case PROGRESS_BEGIN:
                var begun = new TrackedProgress(token, new LoadProgress(Title(value), percentage));
                return (index < 0 ? tracked : tracked.RemoveAt(index)).Add(begun);

            case PROGRESS_REPORT when index >= 0:
                var current = tracked[index];
                return tracked.SetItem(
                    index,
                    current with
                    {
                        Progress = current.Progress with
                        {
                            Percentage = percentage ?? current.Progress.Percentage,
                        },
                    }
                );

            case PROGRESS_END when index >= 0:
                return tracked.RemoveAt(index);

            default:
                return tracked;
        }
    }

    /// <summary>
    /// Drops the trailing dots that Roslyn adds.
    /// </summary>
    private static string Title(WorkDoneProgress value) =>
        value.Title?.TrimEnd('.', '\u2026') ?? string.Empty;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Roslyn: {Message}")]
    private static partial void LogRoslynMessage(ILogger logger, string message);

    private sealed record TrackedProgress(string Token, LoadProgress Progress);
}
