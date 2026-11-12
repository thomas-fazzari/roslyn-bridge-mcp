// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string ReferencesMessage = "RoslynBridge.Extension.ReferencesHandler";
    private const string CallersMessage = "RoslynBridge.Extension.CallersHandler";
    private const string UnusedMessage = "RoslynBridge.Extension.UnusedHandler";
    private const int RequestCancelledCode = -32800;
    private const int ServerCancelledCode = -32802;

    private static readonly string _extensionPath = Path.Combine(
        AppContext.BaseDirectory,
        "extension",
        "RoslynBridge.Extension.dll"
    );

    private static readonly string[] _documentMessages = [ReferencesMessage, CallersMessage];
    private static readonly string[] _workspaceMessages = [UnusedMessage];

    private bool _documentHandlersActive;
    private bool _workspaceHandlersActive;

    /// <summary>
    /// Finds references inside Roslyn, without the batching delay of textDocument/references.
    /// </summary>
    /// <param name="parameters">The textDocument and zero-based position of the request.</param>
    /// <returns>
    /// LSP locations and a count of generated references, or null when the caller must ask
    /// textDocument/references.
    /// </returns>
    public Task<JsonNode?> FindReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) => DispatchDocumentMessageAsync(ReferencesMessage, parameters, cancellationToken);

    /// <summary>
    /// Finds callers inside Roslyn, with the calls through base and interface members.
    /// </summary>
    /// <param name="parameters">The textDocument and zero-based position of the request.</param>
    /// <returns>
    /// LSP incoming calls, or null when the caller must ask callHierarchy/incomingCalls.
    /// </returns>
    public async Task<JsonArray?> FindCallersAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) =>
        (await DispatchDocumentMessageAsync(CallersMessage, parameters, cancellationToken))?[
            "calls"
        ] as JsonArray;

    /// <summary>
    /// Finds the declarations of some files that no live code uses.
    /// </summary>
    /// <param name="files">Full paths of C# files.</param>
    public async Task<Result<JsonNode>> FindUnusedAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        if (!_workspaceHandlersActive)
        {
            return ToolError.InvalidLspData(
                "Listing unused symbols needs the Roslyn extension, which is unavailable in this session."
            );
        }

        // A failure of this long search says nothing about the other handlers, so they stay active
        JsonNode? result;
        try
        {
            result = await RequestAsync(
                LspMethods.WorkspaceDispatchExtensionMessage,
                new JsonObject
                {
                    ["messageName"] = UnusedMessage,
                    ["message"] = new JsonObject
                    {
                        ["files"] = new JsonArray([
                            .. files.Select(file => JsonValue.Create(file)),
                        ]),
                    }.ToJsonString(),
                },
                cancellationToken
            );
        }
        catch (RemoteRpcException exception)
        {
            return UnusedFailure(exception.Message);
        }

        return ExtensionResponse(result) is { } response && JsonNode.Parse(response) is { } found
            ? found
            : UnusedFailure(ExtensionError(result));
    }

    private async Task<JsonNode?> DispatchDocumentMessageAsync(
        string messageName,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (!_documentHandlersActive)
        {
            return null;
        }

        JsonNode? result;
        try
        {
            result = await RequestAsync(
                LspMethods.TextDocumentDispatchExtensionMessage,
                new JsonObject
                {
                    ["messageName"] = messageName,
                    ["message"] = parameters["position"]!.ToJsonString(),
                    ["textDocument"] = parameters["textDocument"]!.DeepClone(),
                },
                cancellationToken
            );
        }
        // Roslyn cancels a request when the workspace changes, which says nothing about the extension
        catch (RemoteInvocationException exception)
            when (exception.ErrorCode is RequestCancelledCode or ServerCancelledCode)
        {
            return null;
        }
        catch (RemoteRpcException exception)
        {
            DisableDocumentHandlers(exception.Message);
            return null;
        }

        if (ExtensionResponse(result) is not { } response)
        {
            DisableDocumentHandlers(ExtensionError(result));
            return null;
        }

        var found = JsonNode.Parse(response);
        return found?["found"]?.GetValue<bool>() is true ? found : null;
    }

    private async Task ActivateExtensionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_extensionPath))
        {
            LogExtensionUnavailable(logger, $"{_extensionPath} is missing");
            return;
        }

        JsonNode? result;
        try
        {
            result = await RequestAsync(
                LspMethods.ServerActivateExtension,
                new JsonObject { ["assemblyFilePath"] = _extensionPath },
                cancellationToken
            );
        }
        // A server without the extension handlers reports an unknown method
        catch (RemoteRpcException exception)
        {
            LogExtensionUnavailable(logger, exception.Message);
            return;
        }

        var unregistered = Unregistered(result?["documentMessageHandlers"], _documentMessages);
        _documentHandlersActive = unregistered.Count == 0;
        if (!_documentHandlersActive)
        {
            LogExtensionUnavailable(logger, ActivationError(result, unregistered));
        }

        unregistered = Unregistered(result?["workspaceMessageHandlers"], _workspaceMessages);
        _workspaceHandlersActive = unregistered.Count == 0;
        if (_documentHandlersActive && !_workspaceHandlersActive)
        {
            LogUnusedUnavailable(logger, ActivationError(result, unregistered));
        }
    }

    // One warning per session is enough, and a broken handler would fail every request the same way
    private void DisableDocumentHandlers(string reason)
    {
        _documentHandlersActive = false;
        LogExtensionFailed(logger, reason);
    }

    private static ToolError UnusedFailure(string reason) =>
        ToolError.InvalidLspData($"The Roslyn extension failed to list unused symbols: {reason}");

    private static List<string> Unregistered(JsonNode? handlers, string[] messages) =>
        [
            .. messages.Where(message =>
                handlers is not JsonArray registered
                || !registered.Any(handler =>
                    string.Equals(handler?.GetValue<string>(), message, StringComparison.Ordinal)
                )
            ),
        ];

    private static string ActivationError(JsonNode? result, List<string> unregistered) =>
        result?["extensionException"]?["Message"]?.GetValue<string>()
        ?? $"Roslyn registered no handler for {string.Join(", ", unregistered)}.";

    // A handler that throws leaves an empty response
    private static string? ExtensionResponse(JsonNode? result) =>
        result?["response"]?.GetValue<string>() is { } response
        && !string.IsNullOrWhiteSpace(response)
            ? response
            : null;

    private static string ExtensionError(JsonNode? result) =>
        result?["extensionException"]?["Message"]?.GetValue<string>()
        ?? (
            result?["extensionWasUnloaded"]?.GetValue<bool>() is true
                ? "Roslyn unloaded the extension."
                : "Roslyn returned no result."
        );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension is unavailable, references and callers will use the LSP requests and unused symbols cannot be listed: {Reason}"
    )]
    private static partial void LogExtensionUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension cannot list unused symbols: {Reason}"
    )]
    private static partial void LogUnusedUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension failed, references and callers will use the LSP requests until the next session: {Reason}"
    )]
    private static partial void LogExtensionFailed(ILogger logger, string reason);
}
