// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Requests;

namespace RoslynBridge.Changes;

[DebuggerDisplay("{Action} created {CreatedAt}, {Snapshot.Files.Count} files")]
internal sealed record PendingChange(
    LspAction Action,
    WorkspaceSnapshot Snapshot,
    JsonObject? Edit,
    JsonObject? Command,
    IReadOnlyList<CodeActionChoice>? Actions = null
)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One selectable entry of a code action listing.
/// </summary>
/// <param name="Scope">The fix-all scope, for an entry that fixes every occurrence in it.</param>
internal sealed record CodeActionChoice(
    string Title,
    string? Kind,
    JsonObject Action,
    string? Scope
);
