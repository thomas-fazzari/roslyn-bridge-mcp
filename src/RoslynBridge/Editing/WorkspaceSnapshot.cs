// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;

namespace RoslynBridge.Editing;

[DebuggerDisplay("{Fingerprints.Count} files, {DocumentVersions.Count} open documents")]
internal sealed record WorkspaceSnapshot(
    IReadOnlyDictionary<string, string> Fingerprints,
    IReadOnlyDictionary<string, int> DocumentVersions
);
