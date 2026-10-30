// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Editing;

/// <param name="Changes">The text edits by document URI.</param>
/// <param name="DocumentChanges">The ordered document operations. A workspace edit uses only one of the two.</param>
internal sealed record WorkspaceEdit(
    Dictionary<string, TextEdit?[]?>? Changes = null,
    DocumentChange?[]? DocumentChanges = null
);

/// <summary>
/// A text document edit, which has no <paramref name="Kind"/>, or a create, rename or delete operation.
/// </summary>
internal sealed record DocumentChange(
    string? Kind = null,
    VersionedDocument? TextDocument = null,
    TextEdit?[]? Edits = null,
    string? Uri = null,
    string? OldUri = null,
    string? NewUri = null,
    FileOperationOptions? Options = null
);

/// <param name="Version">The document version the edit was computed against, or null for any version.</param>
internal sealed record VersionedDocument(string Uri, int? Version = null);

internal sealed record FileOperationOptions(
    bool Overwrite = false,
    bool IgnoreIfExists = false,
    bool IgnoreIfNotExists = false,
    bool Recursive = false
);

internal sealed record TextEdit(LspRange Range, string NewText);
