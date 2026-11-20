// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    public bool SupportsFileRename =>
        Capabilities?["workspace"]?["fileOperations"]?["willRename"] is not null;

    public async Task<Result<LspLocation[]>> DefinitionAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentDefinition,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadLocations);

    public async Task<Result<LspLocation[]>> TypeDefinitionAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentTypeDefinition,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadLocations);

    public async Task<Result<LspLocation[]>> ImplementationAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentImplementation,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadLocations);

    public async Task<Result<LspLocation[]>> ReferencesAsync(
        DocumentPosition position,
        bool includeDeclaration,
        CancellationToken cancellationToken
    )
    {
        var parameters = LspParameters.At(position);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration };
        return (
            await RequestAsync(LspMethods.TextDocumentReferences, parameters, cancellationToken)
        ).Bind(LspAnswers.ReadLocations);
    }

    public async Task<Result<LspHighlight[]>> DocumentHighlightsAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentDocumentHighlight,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(answer => answer.ReadArray<LspHighlight>());

    /// <summary>
    /// Accepts markup, a marked string or an array. Empty when Roslyn has nothing to show.
    /// </summary>
    public async Task<Result<string>> HoverAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentHover,
                LspParameters.At(position),
                cancellationToken
            )
        ).Map(LspAnswers.ReadHover);

    public async Task<Result<LspSymbol[]>> DocumentSymbolsAsync(
        string uri,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentDocumentSymbol,
                LspParameters.Document(uri),
                cancellationToken
            )
        ).Bind(answer => answer.ReadArray<LspSymbol>());

    public async Task<Result<LspSymbol[]>> WorkspaceSymbolsAsync(
        string query,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.WorkspaceSymbol,
                new JsonObject { ["query"] = query },
                cancellationToken
            )
        ).Bind(answer => answer.ReadArray<LspSymbol>());

    public async Task<Result<HierarchyItem[]>> PrepareCallHierarchyAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentPrepareCallHierarchy,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadHierarchy);

    public async Task<Result<HierarchyItem[]>> PrepareTypeHierarchyAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentPrepareTypeHierarchy,
                LspParameters.At(position),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadHierarchy);

    public async Task<Result<LspCall[]>> IncomingCallsAsync(
        HierarchyItem item,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.CallHierarchyIncomingCalls,
                LspParameters.Item(item),
                cancellationToken
            )
        ).Bind(answer => answer.ReadArray<LspCall>());

    public async Task<Result<LspCall[]>> OutgoingCallsAsync(
        HierarchyItem item,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.CallHierarchyOutgoingCalls,
                LspParameters.Item(item),
                cancellationToken
            )
        ).Bind(answer => answer.ReadArray<LspCall>());

    public async Task<Result<HierarchyItem[]>> SupertypesAsync(
        HierarchyItem item,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TypeHierarchySupertypes,
                LspParameters.Item(item),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadHierarchy);

    public async Task<Result<HierarchyItem[]>> SubtypesAsync(
        HierarchyItem item,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TypeHierarchySubtypes,
                LspParameters.Item(item),
                cancellationToken
            )
        ).Bind(LspAnswers.ReadHierarchy);

    /// <summary>
    /// False when Roslyn cannot rename the symbol at the position.
    /// </summary>
    public async Task<Result<bool>> PrepareRenameAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        (
            await RequestAsync(
                LspMethods.TextDocumentPrepareRename,
                LspParameters.At(position),
                cancellationToken
            )
        ).Map(prepared => prepared is JsonObject);

    /// <summary>
    /// Null when Roslyn returns no edit, both for a position without a symbol and for a name it rejects.
    /// </summary>
    public async Task<Result<WorkspaceEdit?>> RenameAsync(
        DocumentPosition position,
        string newName,
        CancellationToken cancellationToken
    )
    {
        var parameters = LspParameters.At(position);
        parameters["newName"] = newName;
        var renamed = await RequestAsync(
            LspMethods.TextDocumentRename,
            parameters,
            cancellationToken
        );
        if (renamed.IsError(out var error, out var answer))
            return error;

        return answer is not JsonObject json
            ? (WorkspaceEdit?)null
            : WorkspaceEdit.Parse(json).Map<WorkspaceEdit?>(edit => edit);
    }

    public async Task<Result<WorkspaceEdit>> WillRenameFilesAsync(
        string oldUri,
        string newUri,
        CancellationToken cancellationToken
    )
    {
        var parameters = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["oldUri"] = oldUri, ["newUri"] = newUri }),
        };
        var renamed = await RequestAsync(
            LspMethods.WorkspaceWillRenameFiles,
            parameters,
            cancellationToken
        );
        return renamed.IsError(out var error, out var answer)
            ? error
            : WorkspaceEdit.Parse(answer as JsonObject);
    }

    public async Task<Result<LspCodeAction[]>> CodeActionsAsync(
        DocumentRange range,
        CancellationToken cancellationToken
    )
    {
        var parameters = LspParameters.In(range);
        // Roslyn computes the diagnostics in the range itself, so the context stays empty
        parameters["context"] = new JsonObject { ["diagnostics"] = new JsonArray() };
        var listed = await RequestAsync(
            LspMethods.TextDocumentCodeAction,
            parameters,
            cancellationToken
        );
        return listed.Map(LspCodeAction.ReadAll);
    }

    public async Task<Result<ResolvedCodeAction>> ResolveCodeActionAsync(
        LspCodeAction codeAction,
        CancellationToken cancellationToken
    )
    {
        var action = (JsonObject)codeAction.Raw.DeepClone();
        var resolution = (codeAction.Scope, action["data"]) switch
        {
            ({ } scope, _) => await RequestAsync(
                LspMethods.CodeActionResolveFixAll,
                new JsonObject
                {
                    ["title"] = action["title"]?.DeepClone(),
                    ["data"] = action["data"]?.DeepClone(),
                    ["scope"] = scope,
                },
                cancellationToken
            ),
            (null, not null) => await RequestAsync(
                LspMethods.CodeActionResolve,
                action,
                cancellationToken
            ),
            _ => action,
        };
        return resolution.Bind(LspAnswers.ReadResolvedCodeAction);
    }
}
