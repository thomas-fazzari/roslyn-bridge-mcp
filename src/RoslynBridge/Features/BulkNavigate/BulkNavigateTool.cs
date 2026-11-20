// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Features.BulkNavigate;

internal sealed class BulkNavigateTool(
    ToolPipeline pipeline,
    IToolHandler<BulkNavigateRequest, ToolResult> handler
) : IRoslynTool
{
    internal const string ActionDescription = "Navigation to perform.";
    private const string ContextDescription =
        "Show the trimmed source line of each result position. Uses more tokens.";
    internal const string InheritedDescription =
        "For members, also list the members that base classes in the workspace pass on, after the type's own.";

    public static string Name => "bulk_navigate";

    public static string Description =>
        "Run up to 16 independent navigate queries in one call, concurrently. A query cannot use another query's result. Results come back in request order. Sections that do not fit are listed with their size.";

    public static bool ReadOnly => true;

    public Task<CallToolResult> BulkNavigateAsync(
        [Description("Independent navigate queries, each with its own action and target.")]
            BulkQuery[] queries,
        [Description(ContextDescription)] bool context = false,
        [Description(Paging.LimitDescription)]
        [Range(1, Paging.MaximumLimit)]
            int limit = Paging.DefaultLimit,
        [Description(Paging.OffsetDescription)] [Range(0, int.MaxValue)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        pipeline.RunAsync(
            handler,
            BulkNavigateRequest.From(queries, context, new Paging(limit, offset)),
            cancellationToken
        );
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BulkQuery
{
    [Description(BulkNavigateTool.ActionDescription)]
    public required NavigationAction Action { get; init; }

    [Description(SymbolTarget.SymbolDescription)]
    public string? Symbol { get; init; }

    [Description(SymbolTarget.FileDescription)]
    public string? File { get; init; }

    [Description(SymbolTarget.LineDescription)]
    public int? Line { get; init; }

    [Description(SymbolTarget.CharacterDescription)]
    public int? Character { get; init; }

    [Description(BulkNavigateTool.InheritedDescription)]
    public bool Inherited { get; init; }

    public Result<NavigateRequest> ToRequest(bool context, Paging page) =>
        NavigateRequest.From(
            Action.ToLspAction(),
            (Symbol, File, Line, Character),
            Inherited,
            context,
            page
        );

    public string Label(int number) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{number} {Action.ToLspAction().Name} {Symbol ?? Position()}"
        );

    private string Position() =>
        (Line, Character) switch
        {
            ({ } line, { } character) => string.Create(
                CultureInfo.InvariantCulture,
                $"{File}:{line}:{character}"
            ),
            ({ } line, null) => string.Create(CultureInfo.InvariantCulture, $"{File}:{line}"),
            _ => File ?? string.Empty,
        };
}
