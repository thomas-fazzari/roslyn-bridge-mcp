// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;

namespace RoslynBridge.Features.Server.Status;

internal sealed class StatusHandler(SettingsMonitor settings, RoslynSession session)
    : IToolHandler<StatusRequest, SessionStatus>
{
    private const int MAXIMUM_MESSAGE_CHARACTERS = 300;
    private const string ELLIPSIS = "...";

    public Task<Result<SessionStatus>> HandleAsync(
        StatusRequest request,
        CancellationToken cancellationToken
    )
    {
        var issues = session.ProjectIssues;
        return Task.FromResult<Result<SessionStatus>>(
            new SessionStatus(
                session.IsRunning,
                session.IsLoading,
                settings.CurrentValue.WorkspaceRoot,
                settings.CurrentValue.ServerPath,
                session.Progress?.Text,
                Lines(issues, ProjectIssueKind.Error),
                Lines(issues, ProjectIssueKind.Warning),
                session.WatcherFailure is { } failure ? Summary(failure) : null
            )
        );
    }

    private static ImmutableArray<string> Lines(
        ImmutableArray<ProjectIssue> issues,
        ProjectIssueKind kind
    ) =>
        [
            .. issues
                .Where(issue => issue.Kind == kind)
                .Select(issue => $"{issue.Project.Text}: {Summary(Message(issue))}")
                .Order(StringComparer.Ordinal),
        ];

    // The line already names the project, so its absolute path in the message adds nothing
    private static string Message(ProjectIssue issue) =>
        issue.Project.WorkspaceFile is { } file
            ? issue.Message.Replace(file.FullPath, issue.Project.Text, StringComparison.Ordinal)
            : issue.Message;

    private static string Summary(string message)
    {
        var line = message.AsSpan().Trim();
        var lineEnd = line.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            line = line[..lineEnd].TrimEnd();

        return line.Length <= MAXIMUM_MESSAGE_CHARACTERS
            ? line.ToString()
            : string.Concat(line[..MAXIMUM_MESSAGE_CHARACTERS], ELLIPSIS);
    }
}
