// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Symbols;
using RoslynBridge.Workspace;

namespace RoslynBridge.Features.Navigate;

internal static class NavigationExtensions
{
    extension(LspLocation[] locations)
    {
        /// <summary>
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public Result<Locations> ToLocations(
            WorkspacePaths paths,
            Paging page,
            bool skipGenerated
        ) => locations.FindLocations(paths, skipGenerated).Map(found => found.Page(page));
    }

    extension(FoundLocations found)
    {
        public Locations Page(Paging page)
        {
            var files = page.Slice(found.Positions)
                .GroupBy(location => location.File, StringComparer.Ordinal)
                .Select(group => new FileLocations(
                    group.Key,
                    [.. group.Select(location => location.Position)]
                ))
                .ToList();
            return new Locations(
                files,
                found.Positions.Count,
                page.NextOffset(found.Positions.Count),
                found.Generated
            );
        }
    }

    extension(IReadOnlyList<SymbolItem> related)
    {
        public RelatedSymbols ToRelatedSymbols(Paging page)
        {
            var items = related
                .OrderBy(item => item.File, StringComparer.Ordinal)
                .ThenBy(item => item.Position?.Line)
                .ThenBy(item => item.Position?.Character)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Through, StringComparer.Ordinal)
                .ToList();
            return new RelatedSymbols(
                [.. page.Slice(items)],
                items.Count,
                page.NextOffset(items.Count)
            );
        }
    }

    extension(Locations locations)
    {
        public Locations WithSourceLines(WorkspacePaths paths)
        {
            var lines = new SourceLines();
            return locations with
            {
                Files =
                [
                    .. locations.Files.Select(file =>
                        file with
                        {
                            Lines = lines.At(paths.ResultFilePath(file.File), file.Positions),
                        }
                    ),
                ],
            };
        }
    }

    extension(RelatedSymbols symbols)
    {
        /// <summary>
        /// Call sites are in <paramref name="callSiteFile"/> when it is set, otherwise in the item's own file.
        /// </summary>
        public RelatedSymbols WithSourceLines(WorkspacePaths paths, string? callSiteFile)
        {
            var lines = new SourceLines();
            return symbols with
            {
                Items =
                [
                    .. symbols.Items.Select(item =>
                    {
                        var ownFile = item.File is null ? null : paths.ResultFilePath(item.File);
                        var (file, positions) =
                            item.Calls.Count > 0
                                ? (callSiteFile ?? ownFile, item.Calls)
                                : (ownFile, item.Position is { } position ? [position] : []);
                        return item with { Lines = lines.At(file, positions) };
                    }),
                ],
            };
        }
    }
}
