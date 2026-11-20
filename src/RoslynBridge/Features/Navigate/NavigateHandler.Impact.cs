// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Navigate;

internal sealed partial class NavigateHandler
{
    private async Task<Result<ToolResult>> ImpactAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    )
    {
        var located = await locator.LocateAsync(request.Target, request.Lookup, cancellationToken);
        if (located.IsError(out var error, out var location))
            return error;

        var positioned = await locator.DocumentPositionAsync(location, cancellationToken);
        if (positioned.IsError(out var positionError, out var documentPosition))
            return positionError;

        var targeted = await TargetsTypeAsync(documentPosition, cancellationToken);
        if (targeted.IsError(out var typeError, out var targetsType))
            return typeError;

        // A type has no callers, and its implementations are its subtypes
        LspAction[] actions = targetsType
            ? [LspAction.References, LspAction.Subtypes]
            : [LspAction.References, LspAction.Callers, LspAction.Implementation];

        return await Concurrently.SectionsAsync(
            [.. actions.Select(action => (action.Name, action))],
            (action, token) => ImpactSectionAsync(request, action, location, token),
            cancellationToken
        );
    }

    private async Task<Result<ToolResult>> ImpactSectionAsync(
        NavigateRequest request,
        LspAction action,
        SourceLocation location,
        CancellationToken cancellationToken
    )
    {
        var section = await HandleAsync(
            request with
            {
                Action = action,
                Target = new ResolvedAt(location),
            },
            cancellationToken
        );

        // Roslyn lists a member that nothing overrides as its own implementation
        return action is not LspAction.Implementation || section is not Locations implementations
            ? section
            : Without(implementations, location);
    }

    private static Locations Without(Locations locations, SourceLocation location)
    {
        var (file, position) = location;
        var files = locations
            .Files.Select(found =>
                found.File == file
                    ? found with
                    {
                        Positions = [.. found.Positions.Where(other => other != position)],
                    }
                    : found
            )
            .Where(found => found.Positions.Count > 0)
            .ToList();
        var removed =
            locations.Files.Sum(found => found.Positions.Count)
            - files.Sum(found => found.Positions.Count);
        return locations with { Files = files, Total = locations.Total - removed };
    }

    // For a member, Roslyn prepares the hierarchy of its containing type, whose name lies elsewhere
    private async Task<Result<bool>> TargetsTypeAsync(
        DocumentPosition target,
        CancellationToken cancellationToken
    )
    {
        var response = await session.PrepareTypeHierarchyAsync(target, cancellationToken);
        if (response.IsError(out var error, out var items))
            return error;

        var file = paths.TryGetWorkspaceFile(target.Uri);

        // Roslyn can write the URI it received in another form, such as file:///c%3A/ for file:///C:/
        return items.Any(item =>
            item.Symbol.Uri is { } uri
            && paths.TryGetWorkspaceFile(uri) is { } itemFile
            && itemFile == file
            && item.Symbol.SelectionRange is { } range
            && range.Contains(target.Position)
        );
    }
}
