// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    internal const string ReferencesMessage = "RoslynBridge.Extension.ReferencesHandler";
    internal const string CallersMessage = "RoslynBridge.Extension.CallersHandler";
    internal const string UnusedMessage = "RoslynBridge.Extension.UnusedHandler";
    internal const int RequestCancelledCode = -32800;
    internal const int ServerCancelledCode = -32802;

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
    public Task<Result<ExtensionReferences>> FindReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) =>
        DispatchDocumentMessageAsync<ExtensionReferences>(
            ReferencesMessage,
            parameters,
            cancellationToken
        );

    /// <summary>
    /// Finds callers inside Roslyn, with the calls through base and interface members.
    /// </summary>
    /// <param name="parameters">The textDocument and zero-based position of the request.</param>
    public Task<Result<ExtensionCallers>> FindCallersAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) =>
        DispatchDocumentMessageAsync<ExtensionCallers>(
            CallersMessage,
            parameters,
            cancellationToken
        );

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
        var searched = await RequestAsync(
            LspMethods.WorkspaceDispatchExtensionMessage,
            new JsonObject
            {
                ["messageName"] = UnusedMessage,
                ["message"] = new JsonObject
                {
                    ["files"] = new JsonArray([.. files.Select(file => JsonValue.Create(file))]),
                }.ToJsonString(),
            },
            cancellationToken
        );
        if (searched.IsError(out var error, out var result))
        {
            return error switch
            {
                { RpcCode: RequestCancelledCode or ServerCancelledCode } =>
                    ToolError.InvalidLspData(
                        "Roslyn cancelled the search for unused symbols. Retry the call."
                    ),
                { Code: ErrorCodes.Lsp } => UnusedFailure(error.Message),
                _ => error,
            };
        }

        return ExtensionResponse(result) is { } response
            ? LspJson.Parse<JsonNode>(response)
            : UnusedFailure(ExtensionError(result));
    }

    // Roslyn cancels a request when the workspace changes, which says nothing about the extension
    private static bool IsCancelled(ToolError error) =>
        error.RpcCode is RequestCancelledCode or ServerCancelledCode;

    private async Task<Result<T>> DispatchDocumentMessageAsync<T>(
        string messageName,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
        where T : IExtensionAnswer<T>
    {
        if (!_documentHandlersActive)
        {
            return T.NotFound;
        }

        var dispatched = await RequestAsync(
            LspMethods.TextDocumentDispatchExtensionMessage,
            new JsonObject
            {
                ["messageName"] = messageName,
                ["message"] = parameters["position"]!.ToJsonString(),
                ["textDocument"] = parameters["textDocument"]!.DeepClone(),
            },
            cancellationToken
        );
        if (dispatched.IsError(out var error, out var result))
        {
            if (IsCancelled(error))
            {
                return T.NotFound;
            }

            if (error is { Code: ErrorCodes.Lsp })
            {
                DisableDocumentHandlers(error.Message);
                return T.NotFound;
            }

            return error;
        }

        if (ExtensionResponse(result) is not { } response)
        {
            DisableDocumentHandlers(ExtensionError(result));
            return T.NotFound;
        }

        if (LspJson.Parse<T>(response).IsError(out var invalid, out var answer))
        {
            DisableDocumentHandlers(invalid.Message);
            return T.NotFound;
        }

        return answer;
    }

    private async Task<Result> ActivateExtensionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_extensionPath))
        {
            LogExtensionUnavailable(logger, $"{_extensionPath} is missing");
            return Result.Success;
        }

        var activated = await RequestAsync(
            LspMethods.ServerActivateExtension,
            new JsonObject { ["assemblyFilePath"] = _extensionPath },
            cancellationToken
        );
        if (activated.IsError(out var error, out var result))
        {
            // A server without the extension handlers reports an unknown method
            if (error is not { Code: ErrorCodes.Lsp })
            {
                return error;
            }

            LogExtensionUnavailable(logger, error.Message);
            return Result.Success;
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

        return Result.Success;
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
