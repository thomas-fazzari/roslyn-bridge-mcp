// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Editing;

/// <summary>
/// A workspace edit whose structure is valid. Whether its files exist or are current is checked when it is planned.
/// </summary>
internal sealed record WorkspaceEdit(IReadOnlyList<DocumentOperation> Operations)
{
    public static WorkspaceEdit Empty { get; } = new([]);

    /// <summary>
    /// Turns <c>changes</c> into text document edits that accept any version. A null edit is empty.
    /// </summary>
    public static Result<WorkspaceEdit> Parse(JsonObject? json)
    {
        if (json is null)
            return Empty;

        if (json.Read<WireEdit>().IsError(out var error, out var wire))
            return error;

        if (wire is { Changes: not null, DocumentChanges: not null })
        {
            return ToolError.InvalidLspData(
                "A workspace edit cannot use both changes and documentChanges."
            );
        }

        if (wire.Changes is { } changes)
        {
            return changes
                .Select(change =>
                    ParseEdits(change.Value)
                        .Map<DocumentOperation>(edits => new TextDocumentEdit(
                            change.Key,
                            Version: null,
                            edits
                        ))
                )
                .All()
                .Map(operations => new WorkspaceEdit(operations));
        }

        return wire.DocumentChanges is { } documentChanges
            ? documentChanges
                .Select(ParseOperation)
                .All()
                .Map(operations => new WorkspaceEdit(operations))
            : Empty;
    }

    private static Result<DocumentOperation> ParseOperation(WireOperation? operation) =>
        operation switch
        {
            null => ToolError.InvalidLspData("Missing document operation."),
            { Kind: null } => ParseTextEdit(operation),
            { Kind: FileOperation.Create } => ParseCreate(operation),
            { Kind: FileOperation.Rename } => ParseRename(operation),
            { Kind: FileOperation.Delete } => ParseDelete(operation),
            _ => ToolError.InvalidLspData($"Unsupported workspace operation: {operation.Kind}"),
        };

    private static Result<DocumentOperation> ParseTextEdit(WireOperation operation) =>
        operation.TextDocument is not { } document
            ? ToolError.InvalidLspData("A document edit needs textDocument.")
            : ParseEdits(operation.Edits)
                .Map<DocumentOperation>(edits => new TextDocumentEdit(
                    document.Uri,
                    document.Version,
                    edits
                ));

    private static Result<DocumentOperation> ParseCreate(WireOperation operation) =>
        operation.Uri is not { } uri
            ? MissingProperty("uri")
            : new CreateFile(uri, operation.Options ?? new FileOperationOptions());

    private static Result<DocumentOperation> ParseRename(WireOperation operation)
    {
        if (operation.OldUri is not { } oldUri)
            return MissingProperty("oldUri");

        return operation.NewUri is not { } newUri
            ? MissingProperty("newUri")
            : new RenameFile(oldUri, newUri, operation.Options ?? new FileOperationOptions());
    }

    private static Result<DocumentOperation> ParseDelete(WireOperation operation)
    {
        if (operation.Options is { Recursive: true })
            return ToolError.InvalidRequest("Recursive delete operations are not supported.");

        return operation.Uri is not { } uri
            ? MissingProperty("uri")
            : new DeleteFile(uri, operation.Options ?? new FileOperationOptions());
    }

    private static Result<TextEdit[]> ParseEdits(TextEdit?[]? edits)
    {
        if (edits is null)
            return ToolError.InvalidLspData("Missing text edits.");

        if (edits.Any(edit => edit is null))
            return ToolError.InvalidLspData("Missing text edit.");

        return edits.OfType<TextEdit>().ToArray();
    }

    private static ToolError MissingProperty(string property) =>
        ToolError.InvalidLspData($"Missing {property}.");

    /// <param name="DocumentChanges">A workspace edit uses only one of the two.</param>
    private sealed record WireEdit(
        Dictionary<string, TextEdit?[]?>? Changes = null,
        WireOperation?[]? DocumentChanges = null
    );

    /// <summary>
    /// A text document edit has no <paramref name="Kind"/>.
    /// </summary>
    private sealed record WireOperation(
        string? Kind = null,
        WireDocument? TextDocument = null,
        TextEdit?[]? Edits = null,
        string? Uri = null,
        string? OldUri = null,
        string? NewUri = null,
        FileOperationOptions? Options = null
    );

    /// <param name="Version">Null accepts any version.</param>
    private sealed record WireDocument(string Uri, int? Version = null);
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
