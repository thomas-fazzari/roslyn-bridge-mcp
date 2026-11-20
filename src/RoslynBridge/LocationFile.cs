// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

/// <summary>
/// The file of a result location.
/// </summary>
internal closed record LocationFile
{
    /// <summary>
    /// The text a result line shows: a workspace-relative path, or the URI of an external or generated document.
    /// </summary>
    public string Text =>
        this switch
        {
            Workspace workspace => workspace.File.RelativePath,
            External external => external.Uri,
        };

    /// <summary>
    /// Null for an external or generated document.
    /// </summary>
    public WorkspaceFilePath? WorkspaceFile => this is Workspace workspace ? workspace.File : null;

    internal sealed record Workspace(WorkspaceFilePath File) : LocationFile;

    internal sealed record External(string Uri) : LocationFile;
}
