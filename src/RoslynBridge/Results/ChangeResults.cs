// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;

namespace RoslynBridge.Results;

/// <summary>
/// An edit that has not been applied, stored under its proposal ID.
/// </summary>
/// <param name="Command">The command a code action runs when it is applied, if any.</param>
internal sealed record ProposalPreview(
    string ProposalId,
    IReadOnlyList<FilePreview> Files,
    JsonNode? Command
) : ToolResult
{
    public bool Truncated => Files.Any(file => file.Truncated);
}

internal sealed record ListedAction(int Index, string? Kind, string Title);

/// <param name="Total">Listed actions before limiting, without hidden ones.</param>
/// <param name="Hidden">Suppress or configure actions left out of the listing.</param>
internal sealed record ActionListing(
    string ProposalId,
    IReadOnlyList<ListedAction> Actions,
    int Total,
    bool Truncated,
    int Hidden
) : ToolResult;
