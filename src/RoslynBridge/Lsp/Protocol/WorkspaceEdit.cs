// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// A workspace edit whose structure is valid. Whether its files exist or are current is checked when it is planned.
/// </summary>
internal sealed record WorkspaceEdit(IReadOnlyList<DocumentOperation> Operations)
{
    public static WorkspaceEdit Empty { get; } = new([]);
}

internal closed record DocumentOperation;

internal sealed record TextDocumentEdit(string Uri, int? Version, IReadOnlyList<TextEdit> Edits)
    : DocumentOperation;

internal sealed record CreateFile(string Uri, FileOperationOptions Options) : DocumentOperation;

internal sealed record RenameFile(string OldUri, string NewUri, FileOperationOptions Options)
    : DocumentOperation;

internal sealed record DeleteFile(string Uri, FileOperationOptions Options) : DocumentOperation;

internal sealed record FileOperationOptions(
    bool Overwrite = false,
    bool IgnoreIfExists = false,
    bool IgnoreIfNotExists = false,
    bool Recursive = false
);

internal sealed record TextEdit(LspRange Range, string NewText);
