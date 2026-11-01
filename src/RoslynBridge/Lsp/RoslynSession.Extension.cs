// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string ReferencesMessage = "RoslynBridge.Extension.ReferencesHandler";

    private static readonly string _extensionPath = Path.Combine(
        AppContext.BaseDirectory,
        "extension",
        "RoslynBridge.Extension.dll"
    );

    private bool _extensionActive;

    /// <summary>
    /// Finds references inside Roslyn, without the batching delay of textDocument/references.
    /// </summary>
    /// <param name="parameters">The textDocument and zero-based position of the request.</param>
    /// <returns>
    /// LSP locations and a count of generated references, or null when the caller must ask
    /// textDocument/references.
    /// </returns>
    public async Task<JsonNode?> FindReferencesAsync(
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
                    ["messageName"] = ReferencesMessage,
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

        var references = JsonNode.Parse(response);
        return references?["found"]?.GetValue<bool>() is true ? references : null;
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
            && handlers.Any(handler =>
                string.Equals(
                    handler?.GetValue<string>(),
                    ReferencesMessage,
                    StringComparison.Ordinal
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
        Message = "The Roslyn extension is unavailable, references will use textDocument/references: {Reason}"
    )]
    private static partial void LogExtensionUnavailable(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The Roslyn extension failed, references will use textDocument/references until the next session: {Reason}"
    )]
    private static partial void LogExtensionFailed(ILogger logger, string reason);
}
