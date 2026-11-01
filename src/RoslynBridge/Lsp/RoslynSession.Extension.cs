// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string ReferencesMessage = "RoslynBridge.Extension.ReferencesHandler";
    private const string CallersMessage = "RoslynBridge.Extension.CallersHandler";

    private static readonly string _extensionPath = Path.Combine(
        AppContext.BaseDirectory,
        "extension",
        "RoslynBridge.Extension.dll"
    );

    private static readonly string[] _extensionMessages = [ReferencesMessage, CallersMessage];

    private bool _extensionActive;

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
    ) => DispatchExtensionAsync(ReferencesMessage, parameters, cancellationToken);

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
        (await DispatchExtensionAsync(CallersMessage, parameters, cancellationToken))?["calls"]
        as JsonArray;

    private async Task<JsonNode?> DispatchExtensionAsync(
        string messageName,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (!_extensionActive)
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
        catch (RemoteRpcException exception)
        {
            DisableExtension(exception.Message);
            return null;
        }

        if (result?["response"]?.GetValue<string>() is not { } response)
        {
            DisableExtension(ExtensionError(result));
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

        _extensionActive =
            result?["documentMessageHandlers"] is JsonArray handlers
            && _extensionMessages.All(message =>
                handlers.Any(handler =>
                    string.Equals(handler?.GetValue<string>(), message, StringComparison.Ordinal)
                )
            );
        if (!_extensionActive)
        {
            LogExtensionUnavailable(logger, ExtensionError(result));
        }
    }

    // One warning per session is enough, and a broken extension would fail every request the same way
    private void DisableExtension(string reason)
    {
        _extensionActive = false;
        LogExtensionFailed(logger, reason);
    }

    private static string ExtensionError(JsonNode? result) =>
        result?["extensionException"]?["Message"]?.GetValue<string>()
        ?? (
            result?["extensionWasUnloaded"]?.GetValue<bool>() is true
                ? "Roslyn unloaded the extension."
                : "Roslyn returned no result."
        );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension is unavailable, references and callers will use the LSP requests: {Reason}"
    )]
    private static partial void LogExtensionUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension failed, references and callers will use the LSP requests until the next session: {Reason}"
    )]
    private static partial void LogExtensionFailed(ILogger logger, string reason);
}
