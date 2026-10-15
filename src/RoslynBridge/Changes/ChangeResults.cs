// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Editing;

namespace RoslynBridge.Changes;

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

/// <param name="Total">Actions before limiting.</param>
internal sealed record ActionListing(
    string ProposalId,
    IReadOnlyList<ListedAction> Actions,
    int Total,
    bool Truncated
) : ToolResult;
