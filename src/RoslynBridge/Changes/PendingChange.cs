// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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

internal sealed record CodeActionChoice(
    string Title,
    string? Kind,
    JsonObject Action,
    string? Scope
);
