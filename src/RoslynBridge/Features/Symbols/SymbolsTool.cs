// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Requests;

namespace RoslynBridge.Features.Symbols;

internal sealed class SymbolsTool(
    ToolPipeline pipeline,
    IToolHandler<SymbolsRequest, SymbolList> symbols,
    IToolHandler<UnusedRequest, UnusedSymbols> unusedSymbols
) : IRoslynTool
{
    private const string UnusedDescription =
        "List the types and members of file that nothing uses, or that only unused code or tests use. Slow on many files.";

    public static string Name => "symbols";

    public static string Description =>
        "List the symbols declared in a C# file, search workspace symbols by name, or list the types and members that no live code uses. Positions are one-based.";

    public static bool ReadOnly => true;

    public Task<CallToolResult> SymbolsAsync(
        [Description(
            "Workspace-relative file path. Omit it to search the workspace. With unused, a path or glob, or omit it for every C# file."
        )]
            string? file = null,
        [Description("Symbol name to search when file is omitted.")] string? query = null,
        [Description(UnusedDescription)] bool unused = false,
        [Description(Paging.LimitDescription)]
        [Range(1, Paging.MaximumLimit)]
            int limit = Paging.DefaultLimit,
        [Description(Paging.OffsetDescription)] [Range(0, int.MaxValue)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        unused
            ? pipeline.RunAsync(
                unusedSymbols,
                UnusedRequest.From(file, query, new Paging(limit, offset)),
                cancellationToken
            )
            : pipeline.RunAsync(
                symbols,
                new SymbolsRequest(file, query, new Paging(limit, offset)),
                cancellationToken
            );
}
