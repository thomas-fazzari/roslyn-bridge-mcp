// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Queries;

/// <summary>
/// Converts Roslyn results to the compact, one-based shapes returned by the tool.
/// </summary>
internal static class LspResultsExtensions
{
    private const string SourceGeneratedScheme = "roslyn-source-generated:";

    extension(JsonNode? result)
    {
        /// <summary>
        /// Reduces hover content to its text, whether Roslyn returns markup, a marked string or an array.
        /// </summary>
        public string ToHoverText() => HoverText(result?["contents"]).Trim();

        /// <summary>
        /// Converts document or workspace symbols. Document symbols keep their nesting and declaration end.
        /// The offset and limit apply to nested symbols too, in document order.
        /// A skipped symbol leaves its listed members at its level.
        /// </summary>
        public SymbolList ToSymbolList(WorkspacePaths paths, int limit, int offset = 0) =>
            result.ReadArray<LspSymbol>().ToSymbolList(paths, limit, offset);

        /// <summary>
        /// Converts call or type hierarchy results, in file and position order. Call results also list their call sites.
        /// </summary>
        public SymbolList ToRelatedSymbols(WorkspacePaths paths, int limit, int offset = 0)
        {
            var related = result as JsonArray ?? [];
            var items = related
                .Select(entry => RelatedSymbol(entry, paths))
                .OrderBy(item => item.File, StringComparer.Ordinal)
                .ThenBy(item => item.Position?.Line)
                .ThenBy(item => item.Position?.Character)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            return new SymbolList(
                [.. items.Skip(offset).Take(limit)],
                items.Count,
                LspRequest.NextOffset(offset, limit, items.Count)
            );
        }

        /// <summary>
        /// Groups locations by file, in path and position order, without duplicates.
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public Locations ToLocations(
            WorkspacePaths paths,
            int limit,
            bool skipGenerated,
            int offset = 0
        ) => result.FindLocations(paths, skipGenerated).Page(limit, offset);

        /// <summary>
        /// Collects locations, in path and position order, without duplicates.
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public FoundLocations FindLocations(WorkspacePaths paths, bool skipGenerated)
        {
            var locations = result switch
            {
                null => [],
                JsonArray => result.Read<LspLocation[]>(),
                JsonObject => [result.Read<LspLocation>()],
                _ => throw new ToolErrorException(
                    ToolError.InvalidLspData("Roslyn returned an invalid location result.")
                ),
            };
            var found = new HashSet<(string File, SourcePosition Position)>();
            var generated = 0;
            foreach (var location in locations)
            {
                var uri =
                    location.ResolvedUri
                    ?? throw new ToolErrorException(
                        ToolError.InvalidLspData("Roslyn returned a location without a URI.")
                    );
                var range =
                    location.ResolvedRange
                    ?? throw new ToolErrorException(
                        ToolError.InvalidLspData("Roslyn returned a location without a position.")
                    );
                if (
                    skipGenerated && uri.StartsWith(SourceGeneratedScheme, StringComparison.Ordinal)
                )
                {
                    generated++;
                    continue;
                }

                found.Add((LocationFile(uri, paths), range.Start.OneBased));
            }

            return new FoundLocations(
                [
                    .. found
                        .OrderBy(location => location.File, StringComparer.Ordinal)
                        .ThenBy(location => location.Position.Line)
                        .ThenBy(location => location.Position.Character),
                ],
                generated
            );
        }

        /// <summary>
        /// Returns the one-based start of each document highlight of the given LSP kind.
        /// </summary>
        public List<SourcePosition> ToHighlightStarts(int kind) =>
            [
                .. LspJson
                    .ReadArray<LspHighlight>(result)
                    .Where(highlight => highlight.Kind == kind)
                    .Select(highlight => highlight.Range.Start.OneBased),
            ];
    }

    extension(LspSymbol[] symbols)
    {
        public SymbolList ToSymbolList(WorkspacePaths paths, int limit, int offset = 0)
        {
            var skipped = offset;
            var remaining = limit;
            var total = CountSymbols(symbols);
            return new SymbolList(
                PageSymbols(symbols, paths, ref skipped, ref remaining),
                total,
                LspRequest.NextOffset(offset, limit, total)
            );
        }
    }

    extension(FoundLocations found)
    {
        /// <summary>
        /// Groups the page of locations that starts at <paramref name="offset"/> by file.
        /// </summary>
        public Locations Page(int limit, int offset)
        {
            var files = found
                .Positions.Skip(offset)
                .Take(limit)
                .GroupBy(location => location.File, StringComparer.Ordinal)
                .Select(group => new FileLocations(
                    group.Key,
                    [.. group.Select(location => location.Position)]
                ))
                .ToList();
            return new Locations(
                files,
                found.Positions.Count,
                LspRequest.NextOffset(offset, limit, found.Positions.Count),
                found.Generated
            );
        }
    }

    extension(LspDiagnostic diagnostic)
    {
        // LSP leaves a missing severity to the client. Reporting it as an error keeps it visible
        public DiagnosticSeverity ToSeverity() =>
            diagnostic.Severity is { } severity && Enum.IsDefined((DiagnosticSeverity)severity)
                ? (DiagnosticSeverity)severity
                : DiagnosticSeverity.Error;

        public Diagnostic ToDiagnostic() =>
            new(
                diagnostic.Range.Start.OneBased,
                diagnostic.ToSeverity(),
                diagnostic.CodeText(),
                diagnostic.Message
            );
    }

    private static SymbolItem RelatedSymbol(JsonNode? entry, WorkspacePaths paths)
    {
        var call = entry.Read<LspCall>();
        var symbol = call.From ?? call.To;
        if (symbol is null)
        {
            return Symbol(entry.Read<LspSymbol>(), paths);
        }

        return Symbol(symbol, paths) with
        {
            Calls = [.. (call.FromRanges ?? []).Select(range => range.Start.OneBased)],
        };
    }

    /// <param name="parent">The name of the document symbol that contains this one, if any.</param>
    private static SymbolItem Symbol(LspSymbol symbol, WorkspacePaths paths, string? parent = null)
    {
        var range = symbol.SelectionRange ?? symbol.Location?.Range ?? symbol.Range;
        var uri = symbol.Location?.Uri ?? symbol.Uri;
        var kind =
            Qualified(symbol.Name, parent ?? ContainingType(symbol.ContainerName))
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
            File = uri is null ? null : LocationFile(uri, paths),
            Position = range?.Start.OneBased,
        };
    }

    private static List<SymbolItem> PageSymbols(
        LspSymbol[] symbols,
        WorkspacePaths paths,
        ref int skipped,
        ref int remaining,
        string? parent = null
    )
    {
        var items = new List<SymbolItem>();
        foreach (var symbol in symbols)
        {
            if (remaining == 0)
            {
                break;
            }

            if (skipped > 0)
            {
                skipped--;
                if (symbol.Children is { } listed)
                {
                    items.AddRange(
                        PageSymbols(listed, paths, ref skipped, ref remaining, symbol.Name)
                    );
                }

                continue;
            }

            remaining--;
            var item = Symbol(symbol, paths, parent);
            // A document symbol range covers the whole declaration, while selectionRange is its name
            if (symbol is { SelectionRange: not null, Range: { } range })
            {
                item = item with { End = range.End.OneBased };
            }

            if (symbol.Children is { Length: > 0 } children)
            {
                item = item with
                {
                    Children = PageSymbols(
                        children,
                        paths,
                        ref skipped,
                        ref remaining,
                        symbol.Name
                    ),
                };
            }

            items.Add(item);
        }

        return items;
    }

    private static int CountSymbols(LspSymbol[] symbols) =>
        symbols.Sum(symbol => 1 + CountSymbols(symbol.Children ?? []));

    private static string? Distinct(string? value, string name) =>
        value is { Length: > 0 } text && !string.Equals(text, name, StringComparison.Ordinal)
            ? text
            : null;

    // Roslyn words some containers as "in Type", which the rendering already says
    private static string? ContainerName(string? container) =>
        container?.StartsWith("in ", StringComparison.Ordinal) is true ? container[3..] : container;

    /// <returns>The name after the name of its type when the type is known, or null for an invalid name.</returns>
    private static SymbolName? Qualified(string name, string? type)
    {
        var member = SymbolName.Parse(name);
        if (member is null || type is null)
        {
            return member;
        }

        return SymbolName.Parse(type)?.Append(member);
    }

    // A workspace symbol names its type as "in Type (project Name)" in the en locale
    private static string? ContainingType(string? container)
    {
        if (container?.StartsWith("in ", StringComparison.Ordinal) is not true)
        {
            return null;
        }

        var project = container.IndexOf(" (project ", StringComparison.Ordinal);
        return project < 0 ? container[3..] : container[3..project];
    }

    private static string HoverText(JsonNode? contents) =>
        contents switch
        {
            JsonArray parts => string.Join("\n\n", parts.Select(HoverText)),
            JsonObject markup => markup["value"]?.GetValue<string>() ?? string.Empty,
            JsonValue text => text.GetValue<string>(),
            _ => string.Empty,
        };

    // Workspace files use relative paths. External and generated locations keep their URI.
    private static string LocationFile(string value, WorkspacePaths paths) =>
        paths.TryGetWorkspaceFile(value) is { } file ? paths.RelativePath(file) : value;
}

/// <param name="Positions">The distinct locations, in path and position order.</param>
/// <param name="Generated">Source-generated locations left out of <paramref name="Positions"/>.</param>
internal sealed record FoundLocations(
    IReadOnlyList<(string File, SourcePosition Position)> Positions,
    int Generated
);
