// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Diagnostics;

namespace RoslynBridge.Editing;

/// <summary>
/// The workspace state Roslyn computed an edit against.
/// </summary>
/// <param name="Files">Stamps of the watched files Roslyn had received.</param>
[DebuggerDisplay("{Files.Count} files, {DocumentVersions.Count} open documents")]
internal sealed record WorkspaceSnapshot(
    IReadOnlyDictionary<string, FileStamp> Files,
    IReadOnlyDictionary<string, int> DocumentVersions
)
{
    /// <summary>
    /// Content fingerprints of the files a preview read, checked again when it is applied.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fingerprints { get; init; } =
        FrozenDictionary<string, string>.Empty;
}
