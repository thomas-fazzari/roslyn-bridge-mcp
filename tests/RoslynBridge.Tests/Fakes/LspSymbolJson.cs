// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;

namespace RoslynBridge.Tests.Fakes;

/// <summary>
/// Builds the LSP symbol payloads that fake servers return.
/// </summary>
internal static class LspSymbolJson
{
    public const int LspClassKind = 5;
    public const int MethodLine = 2;
    public const int MethodCharacter = 11;

    public static JsonObject WorkspaceSymbol(
        string name,
        string path,
        int kind = LspClassKind,
        int line = 0,
        int character = 6
    ) =>
        new()
        {
            ["name"] = name,
            ["kind"] = kind,
            ["location"] = new JsonObject
            {
                ["uri"] = new Uri(path).AbsoluteUri,
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                },
            },
        };

    public static JsonObject DocumentSymbol(string name, params JsonObject[] children) =>
        new()
        {
            ["name"] = name,
            ["kind"] = LspClassKind,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                ["end"] = new JsonObject { ["line"] = 2, ["character"] = 1 },
            },
            ["selectionRange"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 6 },
                ["end"] = new JsonObject { ["line"] = 0, ["character"] = 16 },
            },
            ["children"] = new JsonArray([.. children]),
        };

    public static JsonObject DocumentNamespace(string name, params JsonObject[] children)
    {
        var symbol = DocumentSymbol(name, children);
        symbol["kind"] = LspSymbolKinds.Namespace;
        return symbol;
    }

    public static JsonObject DocumentMethod(string name) =>
        new()
        {
            ["name"] = name,
            ["kind"] = LspSymbolKinds.Method,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = MethodLine, ["character"] = 4 },
                ["end"] = new JsonObject { ["line"] = MethodLine, ["character"] = 30 },
            },
            ["selectionRange"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = MethodLine,
                    ["character"] = MethodCharacter,
                },
                ["end"] = new JsonObject { ["line"] = MethodLine, ["character"] = 20 },
            },
        };

    public static JsonObject MethodSymbol(string name, string path) =>
        WorkspaceSymbol(name, path, LspSymbolKinds.Method, MethodLine, MethodCharacter);

    public static JsonObject DocumentSymbol(
        string name,
        int kind,
        int line,
        params JsonObject[] children
    )
    {
        var position = new JsonObject { ["line"] = line, ["character"] = 4 };
        return new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = new JsonObject
            {
                ["start"] = position.DeepClone(),
                ["end"] = position.DeepClone(),
            },
            ["selectionRange"] = new JsonObject
            {
                ["start"] = position,
                ["end"] = position.DeepClone(),
            },
            ["children"] = new JsonArray([.. children]),
        };
    }
}
