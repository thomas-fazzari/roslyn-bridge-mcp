// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Text.Json.Nodes;

namespace RoslynBridge.Lsp;

/// <summary>
/// Describes the standard LSP features implemented by this bridge.
/// </summary>
internal static class ClientCapabilities
{
    public static JsonObject Create(WorkspacePaths paths)
    {
        var capabilities = JsonNode.Parse(
            """
            {
              "workspace": {
                "configuration": true,
                "workspaceFolders": true,
                "applyEdit": true,
                "didChangeWatchedFiles": { "dynamicRegistration": true },
                "fileOperations": { "willRename": true },
                "workspaceEdit": {
                  "documentChanges": true,
                  "resourceOperations": ["create", "rename", "delete"],
                  "failureHandling": "abort"
                }
              },
              "window": { "workDoneProgress": true },
              "textDocument": {
                "diagnostic": { "relatedDocumentSupport": false },
                "publishDiagnostics": { "relatedInformation": true, "versionSupport": true },
                "hover": { "contentFormat": ["plaintext"] },
                "definition": { "linkSupport": true },
                "implementation": { "linkSupport": true },
                "typeDefinition": { "linkSupport": true },
                "documentSymbol": { "hierarchicalDocumentSymbolSupport": true },
                "rename": { "prepareSupport": true },
                "codeAction": {
                  "dataSupport": true,
                  "disabledSupport": true,
                  "resolveSupport": { "properties": ["edit", "command"] },
                  "codeActionLiteralSupport": {
                    "codeActionKind": {
                      "valueSet": ["quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"]
                    }
                  }
                }
              },
              "general": { "positionEncodings": ["utf-16"] }
            }
            """
        );
        return new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["locale"] = "en",
            ["rootUri"] = paths.RootUri,
            ["workspaceFolders"] = WorkspaceFolders(paths),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "roslyn-bridge-mcp",
                ["version"] = Version(),
            },
            ["capabilities"] = capabilities,
        };
    }

    // The SDK appends "+commit" to the informational version
    private static string? Version() =>
        typeof(ClientCapabilities)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];

    public static JsonArray WorkspaceFolders(WorkspacePaths paths) =>
        [
            (JsonNode)
                new JsonObject { ["uri"] = paths.RootUri, ["name"] = Path.GetFileName(paths.Root) },
        ];
}
