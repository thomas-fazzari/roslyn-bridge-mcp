// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

/// <summary>
/// A normalized full path inside the workspace root.
/// </summary>
/// <remarks>
/// <para>Only <see cref="WorkspacePaths"/> creates one: from caller input through <see cref="WorkspacePaths.Resolve"/>, from a Roslyn location, from a path the bridge or the file watcher produced, or from its own file walk.</para>
/// <para>Only <see cref="WorkspacePaths.Resolve"/> rejects a path behind a symbolic link. The file may not exist.</para>
/// </remarks>
[DebuggerDisplay("{RelativePath}")]
internal sealed record WorkspaceFilePath
{
    internal WorkspaceFilePath(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }

    /// <summary>
    /// Orders by full path with an ordinal comparison.
    /// </summary>
    public static IComparer<WorkspaceFilePath> Order { get; } =
        Comparer<WorkspaceFilePath>.Create(
            static (first, second) => string.CompareOrdinal(first.FullPath, second.FullPath)
        );

    public string FullPath { get; }

    /// <summary>
    /// Relative to the workspace root, with forward slashes, as result lines show it.
    /// </summary>
    public string RelativePath { get; }

    public string ToUri() => WorkspacePaths.FileUri(FullPath);
}
