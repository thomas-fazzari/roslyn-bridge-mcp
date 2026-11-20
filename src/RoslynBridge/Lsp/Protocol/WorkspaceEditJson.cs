// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

internal static class WorkspaceEditJson
{
    extension(WorkspaceEdit)
    {
        /// <summary>
        /// Turns <c>changes</c> into text document edits that accept any version. A null edit is empty.
        /// </summary>
        public static Result<WorkspaceEdit> Parse(JsonObject? json)
        {
            if (json is null)
                return WorkspaceEdit.Empty;

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
                : WorkspaceEdit.Empty;
        }
    }

    private static Result<DocumentOperation> ParseOperation(WireOperation? operation) =>
        operation switch
        {
            null => ToolError.InvalidLspData("Missing document operation."),
            { Kind: null } => ParseTextEdit(operation),
            { Kind: "create" } => ParseCreate(operation),
            { Kind: "rename" } => ParseRename(operation),
            { Kind: "delete" } => ParseDelete(operation),
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

        return edits.Any(edit => edit is null)
            ? ToolError.InvalidLspData("Missing text edit.")
            : edits.OfType<TextEdit>().ToArray();
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
