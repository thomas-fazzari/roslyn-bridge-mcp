// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class SymbolsTool(LspTool lsp)
{
    internal const string Name = "symbols";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "List the symbols declared in a C# file, search workspace symbols by name, or list the types and members that no live code uses. Positions are one-based."
    )]
    public Task<CallToolResult> SymbolsAsync(
        [Description(
            "Workspace-relative file path. Omit it to search the workspace. With unused, a path or glob, or omit it for every C# file."
        )]
            string? file = null,
        [Description("Symbol name to search when file is omitted.")] string? query = null,
        [Description(ToolDescriptions.Unused)] bool unused = false,
        [Description(ToolDescriptions.Limit)] int limit = LspRequest.DefaultResultLimit,
        [Description(ToolDescriptions.Offset)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new LspRequest
            {
                Limit = limit,
                Offset = offset,
                Action = LspAction.Symbols,
                File = file,
                Query = query,
                Unused = unused,
            },
            cancellationToken
        );
}
