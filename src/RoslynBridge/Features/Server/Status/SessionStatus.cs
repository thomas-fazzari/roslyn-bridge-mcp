// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Features.Server.Status;

/// <param name="Server">Null when the bridge finds the server itself.</param>
/// <param name="ProjectErrors">One "PATH: MESSAGE" entry per project.</param>
/// <param name="ProjectWarnings">One "PATH: MESSAGE" entry per project.</param>
/// <param name="WatcherFailure">Null while the file watcher works.</param>
internal sealed record SessionStatus(
    bool Running,
    bool Loading,
    string Workspace,
    string? Server,
    string? Progress = null,
    ImmutableArray<string> ProjectErrors = default,
    ImmutableArray<string> ProjectWarnings = default,
    string? WatcherFailure = null
) : ToolResult
{
    private const int MaximumIssueLines = 10;
    private const string ProjectErrorLabel = "project not loaded";
    private const string ProjectWarningLabel = "project warning";

    internal override void Write(StringBuilder text, int budget)
    {
        text.Append("running: ")
            .Append(Running ? "true" : "false")
            .Append("\nloading: ")
            .Append(Loading ? "true" : "false")
            .Append('\n');
        if (Progress is { } progress)
            text.Append("progress: ").Append(progress).Append('\n');

        text.Append("workspace: ").Append(Workspace).Append('\n');
        if (Server is { } server)
            text.Append("server: ").Append(server).Append('\n');

        AppendIssues(text, ProjectErrorLabel, ProjectErrors);
        AppendIssues(text, ProjectWarningLabel, ProjectWarnings);
        if (WatcherFailure is { } failure)
        {
            text.Append("watcher failed (full rescan before each call): ")
                .Append(failure)
                .Append('\n');
        }
    }

    private static void AppendIssues(StringBuilder text, string label, ImmutableArray<string> lines)
    {
        if (lines.IsDefaultOrEmpty)
            return;

        foreach (var line in lines.Take(MaximumIssueLines))
        {
            text.Append(label).Append(": ").Append(line).Append('\n');
        }

        if (lines.Length > MaximumIssueLines)
        {
            text.Append("truncated: ")
                .Append(lines.Length - MaximumIssueLines)
                .Append(" more ")
                .Append(label)
                .Append(" lines not shown\n");
        }
    }
}
