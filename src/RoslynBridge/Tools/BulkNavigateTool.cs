// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
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
        [Description("Independent navigate queries, each with its own action and target.")]
            BulkQuery[] queries,
        [Description(ToolDescriptions.Context)] bool context = false,
        [Description(ToolDescriptions.Limit)] int limit = Paging.DefaultLimit,
        [Description(ToolDescriptions.Offset)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new BulkRequest(
                [
                    .. queries.Select(query => new NavigationRequest(
                        query.Action.ToLspAction(),
                        new SymbolTarget(query.Symbol, query.File, query.Line, query.Character),
                        query.Inherited,
                        context,
                        new Paging(limit, offset)
                    )),
                ],
                new Paging(limit, offset)
            ),
            cancellationToken
        );
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BulkQuery
{
    [Description(ToolDescriptions.Navigation)]
    public required NavigationAction Action { get; init; }

    [Description(ToolDescriptions.Symbol)]
    public string? Symbol { get; init; }

    [Description(ToolDescriptions.File)]
    public string? File { get; init; }

    [Description(ToolDescriptions.Line)]
    public int? Line { get; init; }

    [Description(ToolDescriptions.Character)]
    public int? Character { get; init; }

    [Description(ToolDescriptions.Inherited)]
    public bool Inherited { get; init; }
}
