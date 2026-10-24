// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;

namespace RoslynBridge.Tests.Fakes;

/// <summary>
/// Answers workspace symbol queries and document symbol requests from fixed tables, so a test
/// describes only the symbols it needs.
/// </summary>
internal static class FakeSymbolServer
{
    /// <param name="workspace">The workspace symbols of each query. Other queries find none.</param>
    /// <param name="documents">The document symbols of each file name. Other files have none.</param>
    /// <returns>A launcher that starts a fake server answering from the tables.</returns>
    public static RoslynLauncher With(
        Dictionary<string, JsonObject[]> workspace,
        Dictionary<string, JsonObject[]>? documents = null
    )
    {
        var byFile = documents ?? new Dictionary<string, JsonObject[]>(StringComparer.Ordinal);
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
        new([.. symbols.GetValueOrDefault(key, []).Select(symbol => symbol.DeepClone())]);

    private static string FileNameOf(JsonNode? parameters) =>
        Path.GetFileName(
            new Uri(parameters!["textDocument"]!["uri"]!.GetValue<string>()).LocalPath
        );
}
