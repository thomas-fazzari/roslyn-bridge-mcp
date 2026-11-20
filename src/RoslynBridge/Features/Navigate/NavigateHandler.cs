// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.Navigate;

internal sealed partial class NavigateHandler(
    RoslynSession session,
    WorkspacePaths paths,
    SymbolResolver resolver,
    SymbolLocator locator
) : IToolHandler<NavigateRequest, ToolResult>
{
    public async Task<Result<ToolResult>> HandleAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    ) =>
        request.Action switch
        {
            LspAction.Source => (await SourceAsync(request, cancellationToken)).AsToolResult(),
            LspAction.Members => (await MembersAsync(request, cancellationToken)).AsToolResult(),
            LspAction.Impact => await ImpactAsync(request, cancellationToken),
            _ => await AtPositionAsync(request, cancellationToken),
        };

    private async Task<Result<ToolResult>> AtPositionAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    )
    {
        var positioned = await locator.PositionAsync(
            request.Target,
            request.Lookup,
            cancellationToken
        );
        if (positioned.IsError(out var error, out var position))
            return error;

        var result = await NavigateAtAsync(request, position, cancellationToken);
        return
            result is ToolResult { IsEmpty: true }
            && await locator.MissingSymbolAsync(position, cancellationToken) is { } missing
            ? missing
            : result;
    }

    private async Task<Result<ToolResult>> NavigateAtAsync(
        NavigateRequest request,
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        request.Action switch
        {
            _ when request.IsHierarchy => await HierarchyAsync(
                request,
                position,
                cancellationToken
            ),
            _ when request.IsAccess || request.Action is LspAction.References => await UsagesAsync(
                request,
                position,
                cancellationToken
            ),
            LspAction.Hover => (
                await session.HoverAsync(position, cancellationToken)
            ).Map<ToolResult>(text => new HoverText(text)),
            LspAction.Definition => DeclarationLocations(
                request,
                await session.DefinitionAsync(position, cancellationToken)
            ),
            LspAction.TypeDefinition => DeclarationLocations(
                request,
                await session.TypeDefinitionAsync(position, cancellationToken)
            ),
            LspAction.Implementation => DeclarationLocations(
                request,
                await session.ImplementationAsync(position, cancellationToken)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported navigation action: {request.Action}."
            ),
        };

    private async Task<Result<ToolResult>> UsagesAsync(
        NavigateRequest request,
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var lookup = await ReferencesAsync(position, cancellationToken);
        if (lookup.IsError(out var error, out var found))
            return error;

        if (request.IsAccess)
            return await AccessAsync(request, found, cancellationToken);

        var references = found.Locations.Page(request.Page);
        return request.Context ? references.WithSourceLines() : references;
    }

    private Result<ToolResult> DeclarationLocations(
        NavigateRequest request,
        Result<LspLocation[]> response
    )
    {
        if (response.IsError(out var error, out var result))
            return error;

        // A generated partial, such as a regex generator's, lists its type a second time
        return result.ToLocations(
            paths,
            request.Page,
            skipGenerated: request.Action is LspAction.Implementation
        ) switch
        {
            ToolError locationsError => locationsError,
            Locations locations when request.Context => locations.WithSourceLines(),
            Locations locations => locations,
        };
    }

    private async Task<Result<ToolResult>> HierarchyAsync(
        NavigateRequest request,
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var callersLookup =
            request.Action is LspAction.Callers
                ? await session.FindCallersAsync(position, cancellationToken)
                : ExtensionCallers.NotFound;
        if (callersLookup.IsError(out var callersError, out var callers))
            return callersError;

        var expansion = callers.Found
            ? callers
                .Calls.ToRelatedItems(paths)
                .Map(related => new Hierarchy(related, Accesses: 0, Prepared: true))
            : await ExpandHierarchyAsync(request.Action, position, cancellationToken);
        if (expansion.IsError(out var hierarchyError, out var hierarchy))
            return hierarchyError;

        var symbols = hierarchy.Related.ToRelatedSymbols(request.Page);
        var named = await resolver.NameAsync(symbols.Items, cancellationToken);
        if (named.IsError(out var namesError, out var items))
            return namesError;

        var result = symbols with
        {
            Items = items,
            Accesses = hierarchy.Accesses,
            Hierarchy = hierarchy.Prepared ? request.Action : null,
        };
        if (!request.Context)
            return result;

        // Outgoing call sites are in the requested document, not in the callee's file
        var callSiteFile =
            request.Action is LspAction.Callees ? paths.TryGetWorkspaceFile(position.Uri) : null;
        return result.WithSourceLines(callSiteFile);
    }

    private Task<Result<Hierarchy>> ExpandHierarchyAsync(
        LspAction action,
        DocumentPosition position,
        CancellationToken cancellationToken
    ) =>
        action is LspAction.Callers or LspAction.Callees
            ? ExpandCallsAsync(action, position, cancellationToken)
            : ExpandTypesAsync(action, position, cancellationToken);

    private async Task<Result<Hierarchy>> ExpandTypesAsync(
        LspAction action,
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var preparation = await session.PrepareTypeHierarchyAsync(position, cancellationToken);
        if (preparation.IsError(out var error, out var items))
            return error;

        var related = new List<SymbolItem>();
        foreach (var item in items)
        {
            var types =
                action is LspAction.Supertypes
                    ? await session.SupertypesAsync(item, cancellationToken)
                    : await session.SubtypesAsync(item, cancellationToken);
            if (types.IsError(out var typesError, out var expanded))
                return typesError;

            related.AddRange(expanded.ToRelatedItems(paths));
        }

        return new Hierarchy(related, Accesses: 0, Prepared: items.Length > 0);
    }

    private async Task<Result<Hierarchy>> ExpandCallsAsync(
        LspAction action,
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var preparation = await session.PrepareCallHierarchyAsync(position, cancellationToken);
        if (preparation.IsError(out var error, out var items))
            return error;

        var related = new List<SymbolItem>();
        var accesses = 0;
        foreach (var item in items)
        {
            var expansion =
                action is LspAction.Callers
                    ? await session.IncomingCallsAsync(item, cancellationToken)
                    : await session.OutgoingCallsAsync(item, cancellationToken);
            if (expansion.IsError(out var expandError, out var expanded))
                return expandError;

            // Outgoing calls also list property, field and constant accesses, which hide the calls
            var invocable =
                action is LspAction.Callees
                    ? [.. expanded.Where(call => LspSymbolKinds.IsInvocable(call.To?.Kind))]
                    : expanded;
            accesses += expanded.Length - invocable.Length;
            if (invocable.ToRelatedItems(paths).IsError(out var callError, out var callItems))
                return callError;

            related.AddRange(callItems);
        }

        return new Hierarchy(related, accesses, Prepared: items.Length > 0);
    }

    /// <param name="Prepared">False when Roslyn found no symbol to expand.</param>
    private readonly record struct Hierarchy(List<SymbolItem> Related, int Accesses, bool Prepared);
}
