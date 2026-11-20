// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// The latest load error or warning that Roslyn logged for a project.
/// </summary>
/// <param name="Project">The project file, which Roslyn may load from outside the workspace.</param>
/// <param name="Message">The first line of the Roslyn message.</param>
internal sealed record ProjectIssue(LocationFile Project, ProjectIssueKind Kind, string Message);

internal enum ProjectIssueKind
{
    Error = 0,
    Warning = 1,
}
