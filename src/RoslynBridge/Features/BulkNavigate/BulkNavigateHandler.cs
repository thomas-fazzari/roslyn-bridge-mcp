// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.BulkNavigate;

internal sealed class BulkNavigateHandler(
    IToolHandler<NavigateRequest, ToolResult> navigate,
    SymbolLocator locator
) : IToolHandler<BulkNavigateRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        BulkNavigateRequest request,
        CancellationToken cancellationToken
    )
    {
        var queries = request.Queries.Select(query => ToRequest(query, request)).ToList();
        var located = await LocateAllAsync(queries, cancellationToken);

        return await Concurrently.SectionsAsync(
            [.. request.Queries.Select((query, index) => (query.Label(index + 1), located[index]))],
            async (query, token) =>
                query switch
                {
                    ToolError error => error,
                    NavigateRequest navigation => await navigate.HandleAsync(navigation, token),
                },
            cancellationToken
        );
    }

    // A name resolves to its declaration whatever the action, so only the type preference splits the lookups
    // A name that several queries look up alike resolves once, and a failure to resolve it fails them all
    private async Task<Result<NavigateRequest>[]> LocateAllAsync(
        List<Result<NavigateRequest>> queries,
        CancellationToken cancellationToken
    )
    {
        var lookups = queries.Values().Where(NeedsLocation).Select(Key).Distinct().ToList();
        var found = await Concurrently.RunAsync(
            lookups,
            (lookup, token) =>
                locator.LocateAsync(
                    lookup.Target,
                    new SymbolLookup(lookup.PrefersTypes, ToDeclaration: false),
                    token
                ),
            cancellationToken
        );
        var locations = lookups.Zip(found).ToDictionary(pair => pair.First, pair => pair.Second);
        return [.. queries.Select(query => Located(query, locations))];
    }

    private static Result<NavigateRequest> Located(
        Result<NavigateRequest> query,
        Dictionary<(SymbolTarget Target, bool PrefersTypes), Result<SourceLocation>> locations
    )
    {
        if (query is not NavigateRequest request || !NeedsLocation(request))
            return query;

        return locations[Key(request)].IsError(out var error, out var found)
            ? error
            : (request with { Target = new ResolvedAt(found) });
    }

    private static (SymbolTarget Target, bool PrefersTypes) Key(NavigateRequest request) =>
        (request.Target, request.Lookup.PrefersTypes);

    private static Result<NavigateRequest> ToRequest(
        BulkQuery query,
        BulkNavigateRequest request
    ) =>
        query.File is { } file && request.FileErrors.GetValueOrDefault(file) is { } error
            ? error
            : query.ToRequest(request.Context, request.Page);

    // source and members resolve declarations themselves
    private static bool NeedsLocation(NavigateRequest query) =>
        query is { Target: NamedSymbol, Action: not (LspAction.Source or LspAction.Members) };
}
