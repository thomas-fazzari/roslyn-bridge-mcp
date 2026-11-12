// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Tests.Fakes;

internal static class FakeSymbolServer
{
    /// <param name="workspace">Other queries find none.</param>
    /// <param name="documents">By file name. Other files have none.</param>
    public static RoslynLauncher With(
        Dictionary<string, JsonObject[]> workspace,
        Dictionary<string, JsonObject[]>? documents = null
    )
    {
        var byFile = documents ?? [with(StringComparer.Ordinal)];
        return (_, _) =>
            new FakeRoslynServer
            {
                Handle = (method, parameters) =>
                    Task.FromResult<JsonNode?>(
                        method switch
                        {
                            LspMethods.WorkspaceSymbol => SymbolsFor(
                                workspace,
                                parameters!["query"]!.GetValue<string>()
                            ),
                            LspMethods.TextDocumentDocumentSymbol => SymbolsFor(
                                byFile,
                                FileNameOf(parameters)
                            ),
                            _ => null,
                        }
                    ),
            };
    }

    private static JsonArray SymbolsFor(Dictionary<string, JsonObject[]> symbols, string key) =>
        [.. symbols.GetValueOrDefault(key, []).Select(symbol => symbol.DeepClone())];

    private static string FileNameOf(JsonNode? parameters) =>
        Path.GetFileName(
            new Uri(parameters!["textDocument"]!["uri"]!.GetValue<string>()).LocalPath
        );
}
