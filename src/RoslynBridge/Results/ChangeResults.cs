// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <param name="Command">The command a code action runs when applied.</param>
internal sealed record ProposalPreview(
    string ProposalId,
    IReadOnlyList<FilePreview> Files,
    JsonNode? Command
) : ToolResult
{
    public bool Truncated => Files.Any(file => file.Truncated);
}

internal sealed record ListedAction(int Index, string? Kind, string Title);

/// <param name="Total">Before limiting, without hidden actions.</param>
/// <param name="Hidden">Suppress or configure actions left out.</param>
internal sealed record ActionListing(
    string ProposalId,
    IReadOnlyList<ListedAction> Actions,
    int Total,
    bool Truncated,
    int Hidden
) : ToolResult;
