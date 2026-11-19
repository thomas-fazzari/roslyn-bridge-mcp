// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Queries;

internal static class LspResultsExtensions
{
    private const string SourceGeneratedScheme = "roslyn-source-generated:";

    extension(JsonNode? result)
    {
        /// <summary>
        /// Accepts markup, a marked string or an array.
        /// </summary>
        public string ToHoverText() => HoverText(result?["contents"]).Trim();

        /// <summary>
        /// The offset and limit apply to nested symbols in document order. A skipped symbol leaves its listed members at its level.
        /// </summary>
        public Result<SymbolList> ToSymbolList(WorkspacePaths paths, int limit, int offset = 0) =>
            result
                .ReadArray<LspSymbol>()
                .Map(symbols => symbols.ToSymbolList(paths, limit, offset));

        /// <summary>
        /// Call results also list their call sites.
        /// </summary>
        public Result<SymbolList> ToRelatedSymbols(WorkspacePaths paths, int limit, int offset = 0)
        {
            var entries = (result as JsonArray ?? []).Select(entry => RelatedSymbol(entry, paths));
            if (entries.All().IsError(out var error, out var related))
                return error;

            var items = related
                .OrderBy(item => item.File, StringComparer.Ordinal)
                .ThenBy(item => item.Position?.Line)
                .ThenBy(item => item.Position?.Character)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Through, StringComparer.Ordinal)
                .ToList();
            return new SymbolList(
                [.. items.Skip(offset).Take(limit)],
                items.Count,
                Paging.NextOffset(offset, limit, items.Count)
            );
        }

        /// <summary>
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public Result<Locations> ToLocations(
            WorkspacePaths paths,
            int limit,
            bool skipGenerated,
            int offset = 0
        ) => result.FindLocations(paths, skipGenerated).Map(found => found.Page(limit, offset));

        public Result<FoundLocations> FindLocations(WorkspacePaths paths, bool skipGenerated)
        {
            var read = result switch
            {
                null => Array.Empty<LspLocation>(),
                JsonArray => result.Read<LspLocation[]>(),
                JsonObject => result.Read<LspLocation>().Map(location => new[] { location }),
                _ => ToolError.InvalidLspData("Roslyn returned an invalid location result."),
            };
            if (read.IsError(out var error, out var locations))
                return error;

            return locations.FindLocations(paths, skipGenerated);
        }
    }

    extension(LspLocation[] locations)
    {
        public Result<FoundLocations> FindLocations(WorkspacePaths paths, bool skipGenerated)
        {
            var found = new HashSet<(string File, SourcePosition Position)>();
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
    }

    extension(LspSymbol symbol)
    {
        public SymbolItem ToSymbolItem(WorkspacePaths paths) => Symbol(symbol, paths);
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
                Paging.NextOffset(offset, limit, total)
            );
        }
    }

    extension(FoundLocations found)
    {
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
                Paging.NextOffset(offset, limit, found.Positions.Count),
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

    private static Result<SymbolItem> RelatedSymbol(JsonNode? entry, WorkspacePaths paths)
    {
        if (entry.Read<LspCall>().IsError(out var error, out var call))
            return error;

        if ((call.From ?? call.To) is not { } symbol)
            return entry.Read<LspSymbol>().Map(found => Symbol(found, paths));

        return Symbol(symbol, paths) with
        {
            Calls = [.. (call.FromRanges ?? []).Select(range => range.Start.OneBased)],
            Through = call.Through,
        };
    }

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
                break;

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
                item = item with { End = range.End.OneBased };

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

    private static SymbolName? Qualified(string name, string? type)
    {
        var member = SymbolName.Parse(name);
        if (member is null || type is null)
            return member;

        return SymbolName.Parse(type)?.Append(member);
    }

    // A workspace symbol names its type as "in Type (project Name)" in the en locale
    private static string? ContainingType(string? container)
    {
        if (container?.StartsWith("in ", StringComparison.Ordinal) is not true)
            return null;

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

/// <param name="Positions">Distinct locations, in path and position order.</param>
internal sealed record FoundLocations(
    IReadOnlyList<(string File, SourcePosition Position)> Positions,
    int Generated
);
