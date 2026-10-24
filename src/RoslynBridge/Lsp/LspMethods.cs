// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// Wire names shared by LSP requests, callbacks and method permissions.
/// </summary>
internal static class LspMethods
{
    public const string CallHierarchyIncomingCalls = "callHierarchy/incomingCalls";
    public const string CallHierarchyOutgoingCalls = "callHierarchy/outgoingCalls";
    public const string ClientRegisterCapability = "client/registerCapability";
    public const string ClientUnregisterCapability = "client/unregisterCapability";
    public const string CodeActionResolve = "codeAction/resolve";
    public const string CodeActionResolveFixAll = "codeAction/resolveFixAll";
    public const string CompletionItemResolve = "completionItem/resolve";
    public const string TextDocumentCodeAction = "textDocument/codeAction";
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    public const string TextDocumentCompletion = "textDocument/completion";
    public const string TextDocumentDefinition = "textDocument/definition";
    public const string TextDocumentDiagnostic = "textDocument/diagnostic";
    public const string TextDocumentGetProjectContexts = "textDocument/_vs_getProjectContexts";
    public const string TextDocumentDidChange = "textDocument/didChange";
    public const string TextDocumentDidClose = "textDocument/didClose";
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    public const string TextDocumentDocumentHighlight = "textDocument/documentHighlight";
    public const string TextDocumentDocumentSymbol = "textDocument/documentSymbol";
    public const string TextDocumentFoldingRange = "textDocument/foldingRange";
    public const string TextDocumentFormatting = "textDocument/formatting";
    public const string TextDocumentHover = "textDocument/hover";
    public const string TextDocumentImplementation = "textDocument/implementation";
    public const string TextDocumentInlayHint = "textDocument/inlayHint";
    public const string TextDocumentPrepareCallHierarchy = "textDocument/prepareCallHierarchy";
    public const string TextDocumentPrepareRename = "textDocument/prepareRename";
    public const string TextDocumentPrepareTypeHierarchy = "textDocument/prepareTypeHierarchy";
    public const string TextDocumentRangeFormatting = "textDocument/rangeFormatting";
    public const string TextDocumentReferences = "textDocument/references";
    public const string TextDocumentRename = "textDocument/rename";
    public const string TextDocumentSelectionRange = "textDocument/selectionRange";
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    public const string TextDocumentSemanticTokensRange = "textDocument/semanticTokens/range";
    public const string TextDocumentSignatureHelp = "textDocument/signatureHelp";
    public const string TextDocumentTypeDefinition = "textDocument/typeDefinition";
    public const string TypeHierarchySubtypes = "typeHierarchy/subtypes";
    public const string TypeHierarchySupertypes = "typeHierarchy/supertypes";
    public const string WindowLogMessage = "window/logMessage";
    public const string WindowShowMessage = "window/showMessage";
    public const string WindowWorkDoneProgressCreate = "window/workDoneProgress/create";
    public const string WorkspaceApplyEdit = "workspace/applyEdit";
    public const string WorkspaceConfiguration = "workspace/configuration";
    public const string WorkspaceDiagnostic = "workspace/diagnostic";
    public const string WorkspaceDidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
    public const string WorkspaceExecuteCommand = "workspace/executeCommand";
    public const string WorkspaceProjectInitializationComplete =
        "workspace/projectInitializationComplete";
    public const string WorkspaceSymbol = "workspace/symbol";
    public const string WorkspaceWillRenameFiles = "workspace/willRenameFiles";
    public const string WorkspaceWorkspaceFolders = "workspace/workspaceFolders";
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string SolutionOpen = "solution/open";
    public const string ProjectOpen = "project/open";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string Progress = "$/progress";
}
