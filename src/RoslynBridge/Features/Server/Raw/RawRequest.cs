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
        LspMethods.TEXT_DOCUMENT_DEFINITION,
        LspMethods.TEXT_DOCUMENT_TYPE_DEFINITION,
        LspMethods.TEXT_DOCUMENT_IMPLEMENTATION,
        LspMethods.TEXT_DOCUMENT_REFERENCES,
        LspMethods.TEXT_DOCUMENT_HOVER,
        LspMethods.TEXT_DOCUMENT_DOCUMENT_HIGHLIGHT,
        LspMethods.TEXT_DOCUMENT_SELECTION_RANGE,
        LspMethods.TEXT_DOCUMENT_INLAY_HINT,
        LspMethods.TEXT_DOCUMENT_FOLDING_RANGE,
        LspMethods.TEXT_DOCUMENT_SEMANTIC_TOKENS_FULL,
        LspMethods.TEXT_DOCUMENT_SEMANTIC_TOKENS_RANGE,
        LspMethods.TEXT_DOCUMENT_CODE_LENS,
        LspMethods.TEXT_DOCUMENT_DOCUMENT_SYMBOL,
        LspMethods.TEXT_DOCUMENT_COMPLETION,
        LspMethods.COMPLETION_ITEM_RESOLVE,
        LspMethods.TEXT_DOCUMENT_SIGNATURE_HELP,
        LspMethods.TEXT_DOCUMENT_CODE_ACTION,
        LspMethods.CODE_ACTION_RESOLVE,
        LspMethods.CODE_ACTION_RESOLVE_FIX_ALL,
        LspMethods.TEXT_DOCUMENT_PREPARE_RENAME,
        LspMethods.TEXT_DOCUMENT_RENAME,
        LspMethods.TEXT_DOCUMENT_DIAGNOSTIC,
        LspMethods.TEXT_DOCUMENT_GET_PROJECT_CONTEXTS,
        LspMethods.WORKSPACE_DIAGNOSTIC,
        LspMethods.WORKSPACE_SYMBOL,
        LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY,
        LspMethods.CALL_HIERARCHY_INCOMING_CALLS,
        LspMethods.CALL_HIERARCHY_OUTGOING_CALLS,
        LspMethods.TEXT_DOCUMENT_PREPARE_TYPE_HIERARCHY,
        LspMethods.TYPE_HIERARCHY_SUPERTYPES,
        LspMethods.TYPE_HIERARCHY_SUBTYPES,
        LspMethods.TEXT_DOCUMENT_FORMATTING,
        LspMethods.TEXT_DOCUMENT_RANGE_FORMATTING,
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
            is LspMethods.INITIALIZE
                or LspMethods.INITIALIZED
                or LspMethods.SHUTDOWN
                or LspMethods.EXIT;

        var isSynchronization =
            method.StartsWith("textDocument/did", StringComparison.Ordinal)
            || method.StartsWith("workspace/did", StringComparison.Ordinal);

        return isLifecycle || isSynchronization || method is LspMethods.WORKSPACE_APPLY_EDIT;
    }
}
