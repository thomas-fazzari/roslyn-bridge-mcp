// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;

namespace RoslynBridge.Lsp;

/// <summary>
/// Describes the standard LSP features implemented by this bridge.
/// </summary>
internal static class ClientCapabilities
{
    public static JsonObject Create(WorkspacePaths paths)
    {
        using var file = typeof(ClientCapabilities).Assembly.GetManifestResourceStream(
            typeof(ClientCapabilities),
            "ClientCapabilities.json"
        )!;
        var capabilities = JsonNode.Parse(file);
        return new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["locale"] = "en",
            ["rootUri"] = paths.RootUri,
            ["workspaceFolders"] = WorkspaceFolders(paths),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "roslyn-bridge-mcp",
                ["version"] = Program.Version,
            },
            ["capabilities"] = capabilities,
        };
    }

    public static JsonArray WorkspaceFolders(WorkspacePaths paths) =>
        [
            (JsonNode)
                new JsonObject { ["uri"] = paths.RootUri, ["name"] = Path.GetFileName(paths.Root) },
        ];
}
