// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// Answers the requests of one Roslyn process and keeps the state they report.
/// </summary>
internal sealed partial class ClientCallbacks(
    RoslynSession session,
    WorkspacePaths paths,
    ILogger logger
)
{
    private const int MaximumLogMessageCharacters = 2048;
    private const string ProgressBegin = "begin";
    private const string ProgressReport = "report";
    private const string ProgressEnd = "end";

    private readonly TaskCompletionSource _initialized = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private readonly ConcurrentDictionary<string, byte> _watchRegistrations = new(
        StringComparer.Ordinal
    );

    // Replaced as a whole, so readers never wait for a notification
    private ImmutableList<TrackedProgress> _progress = [];

    internal Task Initialized => _initialized.Task;

    internal bool WatchesFiles => !_watchRegistrations.IsEmpty;

    /// <summary>
    /// The latest unfinished progress, otherwise null.
    /// </summary>
    internal LoadProgress? Progress => Volatile.Read(ref _progress).LastOrDefault()?.Progress;

    [JsonRpcMethod(
        LspMethods.WorkspaceConfiguration,
        UseSingleObjectParameterDeserialization = true
    )]
    public static object?[] Configuration(ConfigurationParams parameters) =>
        new object?[parameters.Items.Length];

    [JsonRpcMethod(LspMethods.WorkspaceWorkspaceFolders)]
    public JsonArray WorkspaceFolders() => ClientCapabilities.WorkspaceFolders(paths);

    [JsonRpcMethod(LspMethods.WorkspaceApplyEdit, UseSingleObjectParameterDeserialization = true)]
    public Task<ApplyWorkspaceEditResult> ApplyEditAsync(
        ApplyWorkspaceEditParams parameters,
        CancellationToken cancellationToken
    )
    {
        if (session.ApplyWorkspaceEditAsync is { } apply)
        {
            return apply(parameters.Edit, cancellationToken);
        }

        return Task.FromResult(
            new ApplyWorkspaceEditResult(
                Applied: false,
                "No workspace edit is being applied by the current tool call."
            )
        );
    }

    [JsonRpcMethod(
        LspMethods.ClientRegisterCapability,
        UseSingleObjectParameterDeserialization = true
    )]
    public void RegisterCapability(RegistrationParams parameters)
    {
        foreach (var registration in parameters.Registrations)
        {
            if (
                string.Equals(
                    registration.Method,
                    LspMethods.WorkspaceDidChangeWatchedFiles,
                    StringComparison.Ordinal
                )
            )
            {
                _watchRegistrations[registration.Id] = 0;
            }
        }
    }

    [JsonRpcMethod(
        LspMethods.ClientUnregisterCapability,
        UseSingleObjectParameterDeserialization = true
    )]
    public void UnregisterCapability(UnregistrationParams parameters)
    {
        foreach (var registration in parameters.Unregisterations)
        {
            _watchRegistrations.TryRemove(registration.Id, out _);
        }
    }

    [JsonRpcMethod(LspMethods.WorkspaceProjectInitializationComplete)]
    public void InitializationComplete() => _initialized.TrySetResult();

    // Roslyn waits for this answer before it reports progress
    [JsonRpcMethod(
        LspMethods.WindowWorkDoneProgressCreate,
        UseSingleObjectParameterDeserialization = true
    )]
    public static void CreateProgress(JsonObject _) { }

    [JsonRpcMethod(LspMethods.Progress, UseSingleObjectParameterDeserialization = true)]
    public void ReportProgress(ProgressParams parameters)
    {
        var token = parameters.Token.ToString();
        ImmutableInterlocked.Update(ref _progress, tracked =>
            WithProgress(tracked, token, parameters.Value)
        );
    }

    [JsonRpcMethod(LspMethods.WindowLogMessage, UseSingleObjectParameterDeserialization = true)]
    public void LogMessage(MessageParams parameters)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var message = parameters.Message;
        var boundedMessage = message
            .AsSpan(0, Math.Min(message.Length, MaximumLogMessageCharacters))
            .ToString();
        LogRoslynMessage(logger, boundedMessage);
    }

    [JsonRpcMethod(LspMethods.WindowShowMessage, UseSingleObjectParameterDeserialization = true)]
    public void ShowMessage(MessageParams parameters) => LogMessage(parameters);

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
            case ProgressBegin:
                var begun = new TrackedProgress(token, new LoadProgress(Title(value), percentage));
                return (index < 0 ? tracked : tracked.RemoveAt(index)).Add(begun);

            case ProgressReport when index >= 0:
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

            case ProgressEnd when index >= 0:
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
