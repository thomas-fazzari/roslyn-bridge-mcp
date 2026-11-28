// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Symbols;

internal static class LspResultsExtensions
{
    private const string SOURCE_GENERATED_SCHEME = "roslyn-source-generated:";

    extension(LspCall[] calls)
    {
        /// <summary>
        /// Call results also list their call sites.
        /// </summary>
        public Result<List<SymbolItem>> ToRelatedItems(WorkspacePaths paths) =>
            calls.Select(call => RelatedSymbol(call, paths)).All();
    }

    extension(HierarchyItem[] items)
    {
        public List<SymbolItem> ToRelatedItems(WorkspacePaths paths) =>
            [.. items.Select(item => Symbol(item.Symbol, paths))];
    }

    extension(LspLocation[] locations)
    {
        /// <summary>
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public Result<FoundLocations> FindLocations(WorkspacePaths paths, bool skipGenerated)
        {
            var found = new HashSet<SourceLocation>();
            // Resolving a file checks each folder on disk, and a reference list repeats files
            var files = new Dictionary<string, LocationFile>(StringComparer.Ordinal);
            var generated = 0;
            foreach (var location in locations)
            {
                if (location.ResolvedUri is not { } uri)
                    return ToolError.InvalidLspData("Roslyn returned a location without a URI.");

                if (location.ResolvedRange is not { } range)
                {
                    return ToolError.InvalidLspData(
                        "Roslyn returned a location without a position."
                    );
                }

                if (
                    skipGenerated
                    && uri.StartsWith(SOURCE_GENERATED_SCHEME, StringComparison.Ordinal)
                )
                {
                    generated++;
                    continue;
                }

                if (!files.TryGetValue(uri, out var file))
                {
                    file = paths.LocationOf(uri);
                    files.Add(uri, file);
                }

                found.Add(new SourceLocation(file, range.Start.OneBased));
            }

            return new FoundLocations([.. found.Order(SourceLocation.Order)], generated);
        }
    }

    extension(LspSymbol symbol)
    {
        /// <param name="parent">The name of the containing symbol, which corrects the kind of a member.</param>
        public SymbolItem ToSymbolItem(WorkspacePaths paths, string? parent = null) =>
            Symbol(symbol, paths, parent);
    }

    private static Result<SymbolItem> RelatedSymbol(LspCall call, WorkspacePaths paths) =>
        (call.From ?? call.To) is not { } symbol
            ? ToolError.InvalidLspData("Roslyn returned a call without a symbol.")
            : (
                Symbol(symbol, paths) with
                {
                    Calls = [.. (call.FromRanges ?? []).Select(range => range.Start.OneBased)],
                    Through = call.Through,
                }
            );

    private static SymbolItem Symbol(LspSymbol symbol, WorkspacePaths paths, string? parent = null)
    {
        var range = symbol.SelectionRange ?? symbol.Location?.Range ?? symbol.Range;
        var uri = symbol.Location?.Uri ?? symbol.Uri;
        var kind = Qualified(symbol.Name, parent ?? ContainingType(symbol.ContainerName))
            ?.CorrectedKind(symbol.Kind)
            ?? symbol.Kind;

        return new SymbolItem(
            symbol.Name,
            LspSymbolKinds.TryGetName(kind, out var kindName) ? kindName : null
        )
        {
            // Roslyn often repeats the name as the detail
            Detail = Distinct(symbol.Detail, symbol.Name),
            Container = ContainerName(Distinct(symbol.ContainerName, symbol.Name)),
            File = uri is null ? null : paths.LocationOf(uri),
            Position = range?.Start.OneBased,
        };
    }

    private static string? Distinct(string? value, string name) =>
        value is { Length: > 0 } text && !string.Equals(text, name, StringComparison.Ordinal)
            ? text
            : null;

    // Roslyn words some containers as "in Type", which the rendering already says
    private static string? ContainerName(string? container) =>
        container?.StartsWith("in ", StringComparison.Ordinal) is true ? container[3..] : container;

    private static SymbolName? Qualified(string name, string? type)
    {
        var member = SymbolName.Parse(name);
        return member is null || type is null ? member : (SymbolName.Parse(type)?.Append(member));
    }

    // A workspace symbol names its type as "in Type (project Name)" in the en locale
    private static string? ContainingType(string? container)
    {
        if (container?.StartsWith("in ", StringComparison.Ordinal) is not true)
            return null;

        var project = container.IndexOf(" (project ", StringComparison.Ordinal);
        return project < 0 ? container[3..] : container[3..project];
    }
}

/// <param name="Positions">Distinct locations, in path and position order.</param>
internal sealed record FoundLocations(IReadOnlyList<SourceLocation> Positions, int Generated);
