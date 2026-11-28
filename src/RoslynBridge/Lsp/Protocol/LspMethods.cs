// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

internal static class LspMethods
{
    public const string CALL_HIERARCHY_INCOMING_CALLS = "callHierarchy/incomingCalls";
    public const string CALL_HIERARCHY_OUTGOING_CALLS = "callHierarchy/outgoingCalls";
    public const string CLIENT_REGISTER_CAPABILITY = "client/registerCapability";
    public const string CLIENT_UNREGISTER_CAPABILITY = "client/unregisterCapability";
    public const string CODE_ACTION_RESOLVE = "codeAction/resolve";
    public const string CODE_ACTION_RESOLVE_FIX_ALL = "codeAction/resolveFixAll";
    public const string COMPLETION_ITEM_RESOLVE = "completionItem/resolve";
    public const string TEXT_DOCUMENT_CODE_ACTION = "textDocument/codeAction";
    public const string TEXT_DOCUMENT_CODE_LENS = "textDocument/codeLens";
    public const string TEXT_DOCUMENT_COMPLETION = "textDocument/completion";
    public const string TEXT_DOCUMENT_DEFINITION = "textDocument/definition";
    public const string TEXT_DOCUMENT_DIAGNOSTIC = "textDocument/diagnostic";
    public const string TEXT_DOCUMENT_GET_PROJECT_CONTEXTS = "textDocument/_vs_getProjectContexts";
    public const string TEXT_DOCUMENT_DID_CHANGE = "textDocument/didChange";
    public const string TEXT_DOCUMENT_DID_CLOSE = "textDocument/didClose";
    public const string TEXT_DOCUMENT_DID_OPEN = "textDocument/didOpen";
    public const string TEXT_DOCUMENT_DOCUMENT_HIGHLIGHT = "textDocument/documentHighlight";
    public const string TEXT_DOCUMENT_DOCUMENT_SYMBOL = "textDocument/documentSymbol";
    public const string TEXT_DOCUMENT_FOLDING_RANGE = "textDocument/foldingRange";
    public const string TEXT_DOCUMENT_FORMATTING = "textDocument/formatting";
    public const string TEXT_DOCUMENT_HOVER = "textDocument/hover";
    public const string TEXT_DOCUMENT_IMPLEMENTATION = "textDocument/implementation";
    public const string TEXT_DOCUMENT_INLAY_HINT = "textDocument/inlayHint";
    public const string TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY = "textDocument/prepareCallHierarchy";
    public const string TEXT_DOCUMENT_PREPARE_RENAME = "textDocument/prepareRename";
    public const string TEXT_DOCUMENT_PREPARE_TYPE_HIERARCHY = "textDocument/prepareTypeHierarchy";
    public const string TEXT_DOCUMENT_RANGE_FORMATTING = "textDocument/rangeFormatting";
    public const string TEXT_DOCUMENT_REFERENCES = "textDocument/references";
    public const string TEXT_DOCUMENT_RENAME = "textDocument/rename";
    public const string TEXT_DOCUMENT_SELECTION_RANGE = "textDocument/selectionRange";
    public const string TEXT_DOCUMENT_SEMANTIC_TOKENS_FULL = "textDocument/semanticTokens/full";
    public const string TEXT_DOCUMENT_SEMANTIC_TOKENS_RANGE = "textDocument/semanticTokens/range";
    public const string TEXT_DOCUMENT_SIGNATURE_HELP = "textDocument/signatureHelp";
    public const string TEXT_DOCUMENT_TYPE_DEFINITION = "textDocument/typeDefinition";
    public const string TYPE_HIERARCHY_SUBTYPES = "typeHierarchy/subtypes";
    public const string TYPE_HIERARCHY_SUPERTYPES = "typeHierarchy/supertypes";
    public const string WINDOW_LOG_MESSAGE = "window/logMessage";
    public const string WINDOW_SHOW_MESSAGE = "window/showMessage";
    public const string WINDOW_WORK_DONE_PROGRESS_CREATE = "window/workDoneProgress/create";
    public const string WORKSPACE_APPLY_EDIT = "workspace/applyEdit";
    public const string WORKSPACE_CONFIGURATION = "workspace/configuration";
    public const string WORKSPACE_DIAGNOSTIC = "workspace/diagnostic";
    public const string WORKSPACE_DID_CHANGE_WATCHED_FILES = "workspace/didChangeWatchedFiles";
    public const string WORKSPACE_EXECUTE_COMMAND = "workspace/executeCommand";
    public const string WORKSPACE_PROJECT_INITIALIZATION_COMPLETE =
        "workspace/projectInitializationComplete";
    public const string WORKSPACE_SYMBOL = "workspace/symbol";
    public const string WORKSPACE_WILL_RENAME_FILES = "workspace/willRenameFiles";
    public const string WORKSPACE_WORKSPACE_FOLDERS = "workspace/workspaceFolders";
    public const string INITIALIZE = "initialize";
    public const string INITIALIZED = "initialized";
    public const string SOLUTION_OPEN = "solution/open";
    public const string PROJECT_OPEN = "project/open";
    public const string SERVER_ACTIVATE_EXTENSION = "server/_vs_activateExtension";
    public const string TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE =
        "textDocument/_vs_dispatchExtensionMessage";
    public const string WORKSPACE_DISPATCH_EXTENSION_MESSAGE =
        "workspace/_vs_dispatchExtensionMessage";
    public const string SHUTDOWN = "shutdown";
    public const string EXIT = "exit";
    public const string PROGRESS = "$/progress";
}
