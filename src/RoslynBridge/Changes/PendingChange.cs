// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;
using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Changes;

/// <summary>
/// A change computed against <paramref name="Snapshot"/>, which a later call previews or applies.
/// </summary>
[DebuggerDisplay("{Action} created {CreatedAt}, {Snapshot.Files.Count} files")]
internal closed record PendingChange(LspAction Action, WorkspaceSnapshot Snapshot)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An edit that Roslyn computed, with the command that completes it.
/// </summary>
internal sealed record ProposedEdit(
    LspAction Action,
    WorkspaceSnapshot Snapshot,
    WorkspaceEdit Edit,
    LspCommand? Command = null
) : PendingChange(Action, Snapshot);

/// <summary>
/// Code actions listed for a selection. The caller picks one by its index.
/// </summary>
internal sealed record ActionChoices(
    LspAction Action,
    WorkspaceSnapshot Snapshot,
    IReadOnlyList<LspCodeAction> Actions
) : PendingChange(Action, Snapshot);
