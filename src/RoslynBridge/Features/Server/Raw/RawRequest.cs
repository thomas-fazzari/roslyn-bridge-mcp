// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;

namespace RoslynBridge.Features.Server.Raw;

/// <param name="Apply">Allows a method that may change the workspace.</param>
internal sealed record RawRequest(string Method, JsonObject? Parameters, bool Apply) : ToolRequest
{
    private static readonly FrozenSet<string> _readOnlyMethods = new[]
    {
        LspMethods.TextDocumentDefinition,
        LspMethods.TextDocumentTypeDefinition,
        LspMethods.TextDocumentImplementation,
        LspMethods.TextDocumentReferences,
        LspMethods.TextDocumentHover,
        LspMethods.TextDocumentDocumentHighlight,
        LspMethods.TextDocumentSelectionRange,
        LspMethods.TextDocumentInlayHint,
        LspMethods.TextDocumentFoldingRange,
        LspMethods.TextDocumentSemanticTokensFull,
        LspMethods.TextDocumentSemanticTokensRange,
        LspMethods.TextDocumentCodeLens,
        LspMethods.TextDocumentDocumentSymbol,
        LspMethods.TextDocumentCompletion,
        LspMethods.CompletionItemResolve,
        LspMethods.TextDocumentSignatureHelp,
        LspMethods.TextDocumentCodeAction,
        LspMethods.CodeActionResolve,
        LspMethods.CodeActionResolveFixAll,
        LspMethods.TextDocumentPrepareRename,
        LspMethods.TextDocumentRename,
        LspMethods.TextDocumentDiagnostic,
        LspMethods.TextDocumentGetProjectContexts,
        LspMethods.WorkspaceDiagnostic,
        LspMethods.WorkspaceSymbol,
        LspMethods.TextDocumentPrepareCallHierarchy,
        LspMethods.CallHierarchyIncomingCalls,
        LspMethods.CallHierarchyOutgoingCalls,
        LspMethods.TextDocumentPrepareTypeHierarchy,
        LspMethods.TypeHierarchySupertypes,
        LspMethods.TypeHierarchySubtypes,
        LspMethods.TextDocumentFormatting,
        LspMethods.TextDocumentRangeFormatting,
    }.ToFrozenSet(StringComparer.Ordinal);

    public override CallPolicy Policy => new(LspAction.Request, MayRestart: Apply);

    public static Result<RawRequest> From(string? method, JsonObject? parameters, bool apply)
    {
        if (string.IsNullOrWhiteSpace(method))
            return ToolError.InvalidRequest("Provide the LSP method for action=request.");

        if (IsManagedMethod(method))
        {
            return ToolError.InvalidRequest(
                "The bridge owns lifecycle and synchronization methods."
            );
        }

        return _readOnlyMethods.Contains(method) || apply
            ? new RawRequest(method, parameters, apply)
            : ToolError.InvalidRequest(
                "This method may change the workspace. Set apply=true explicitly."
            );
    }

    private static bool IsManagedMethod(string method)
    {
        var isLifecycle =
            method
            is LspMethods.Initialize
                or LspMethods.Initialized
                or LspMethods.Shutdown
                or LspMethods.Exit;

        var isSynchronization =
            method.StartsWith("textDocument/did", StringComparison.Ordinal)
            || method.StartsWith("workspace/did", StringComparison.Ordinal);

        return isLifecycle || isSynchronization || method is LspMethods.WorkspaceApplyEdit;
    }
}
