// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Editing;

[DebuggerDisplay("{Files.Count} files, {DocumentVersions.Count} open documents")]
internal sealed record WorkspaceSnapshot(
    IReadOnlyDictionary<WorkspaceFilePath, FileStamp> Files,
    IReadOnlyDictionary<WorkspaceFilePath, int> DocumentVersions
)
{
    /// <summary>
    /// Content fingerprints of the files a preview read. Checked again on apply.
    /// </summary>
    public IReadOnlyDictionary<WorkspaceFilePath, string> Fingerprints { get; init; } =
        FrozenDictionary<WorkspaceFilePath, string>.Empty;
}
