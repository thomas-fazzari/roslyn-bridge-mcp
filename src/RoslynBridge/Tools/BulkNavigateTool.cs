// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class BulkNavigateTool(LspTool lsp)
{
    internal const string Name = "bulk_navigate";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Run up to 16 independent navigate queries in one call, concurrently. A query cannot use another query's result. Results come back in request order. Sections that do not fit are listed with their size."
    )]
    public Task<CallToolResult> BulkNavigateAsync(
        BulkQuery[] queries,
        [Description(ToolDescriptions.Context)] bool context = false,
        [Description(ToolDescriptions.Limit)] int limit = LspRequest.DefaultResultLimit,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Bulk,
                Limit = limit,
                Context = context,
                Queries =
                [
                    .. queries.Select(query => new LspRequest
                    {
                        Action = query.Action.ToLspAction(),
                        Symbol = query.Symbol,
                        File = query.File,
                        Line = query.Line,
                        Character = query.Character,
                    }),
                ],
            },
            cancellationToken
        );
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BulkQuery
{
    public required NavigationAction Action { get; init; }

    public string? Symbol { get; init; }

    public string? File { get; init; }

    public int? Line { get; init; }

    public int? Character { get; init; }
}
