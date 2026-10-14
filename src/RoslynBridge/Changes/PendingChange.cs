// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Requests;

namespace RoslynBridge.Changes;

[DebuggerDisplay("{Action} created {CreatedAt}, {Snapshot.Fingerprints.Count} files")]
internal sealed record PendingChange(
    LspAction Action,
    WorkspaceSnapshot Snapshot,
    JsonObject? Edit,
    JsonObject? Command,
    JsonArray? Actions = null,
    bool RenamesFiles = false
)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}
