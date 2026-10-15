// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;

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
        /// The limit applies to nested symbols too, in document order.
        /// </summary>
        public SymbolList ToSymbolList(WorkspacePaths paths, int limit)
        {
            var symbols = result as JsonArray ?? [];
            var remaining = limit;
            var total = CountSymbols(symbols);
            return new SymbolList(
                LimitSymbols(symbols, paths, ref remaining),
                total,
                total > limit
            );
        }

        /// <summary>
        /// Converts call or type hierarchy results. Call results also list their call sites.
        /// </summary>
        public SymbolList ToRelatedSymbols(WorkspacePaths paths, int limit)
        {
            var related = result as JsonArray ?? [];
            var items = related
                .Take(limit)
                .Select(entry =>
                    Symbol(entry!["from"] ?? entry["to"] ?? entry, paths) with
                    {
                        Calls = entry["fromRanges"] is JsonArray ranges
                            ? [.. ranges.Select(range => OneBased(range!["start"]!))]
                            : [],
                    }
                )
                .ToList();
            return new SymbolList(items, related.Count, related.Count > limit);
        }

        /// <summary>
        /// Groups locations by file, in path and position order, without duplicates.
        /// With <paramref name="skipGenerated"/>, source-generated locations are only counted.
        /// </summary>
        public Locations ToLocations(WorkspacePaths paths, int limit, bool skipGenerated)
        {
            var locations = result switch
            {
                null => [],
                JsonArray array => array,
                JsonObject location => new JsonArray(location.DeepClone()),
                _ => throw new ToolErrorException(
                    ToolError.InvalidLspData("Roslyn returned an invalid location result.")
                ),
            };
            var found = new HashSet<(string File, SourcePosition Position)>();
            var generated = 0;
            foreach (var location in locations)
            {
                var uri =
                    (location?["uri"] ?? location?["targetUri"])?.GetValue<string>()
                    ?? throw new ToolErrorException(
                        ToolError.InvalidLspData("Roslyn returned a location without a URI.")
                    );
                var start =
                    (location?["range"] ?? location?["targetSelectionRange"])?["start"]
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

                found.Add((LocationFile(uri, paths), OneBased(start)));
            }

            var files = found
                .OrderBy(location => location.File, StringComparer.Ordinal)
                .ThenBy(location => location.Position.Line)
                .ThenBy(location => location.Position.Character)
                .Take(limit)
                .GroupBy(location => location.File, StringComparer.Ordinal)
                .Select(group => new FileLocations(
                    group.Key,
                    [.. group.Select(location => location.Position)]
                ))
                .ToList();
            return new Locations(files, found.Count, found.Count > limit, generated);
        }
    }

    extension(JsonNode diagnostic)
    {
        // LSP leaves a missing severity to the client. Reporting it as an error keeps it visible
        public DiagnosticSeverity Severity() =>
            diagnostic["severity"]?.GetValue<int>() is { } severity
            && Enum.IsDefined((DiagnosticSeverity)severity)
                ? (DiagnosticSeverity)severity
                : DiagnosticSeverity.Error;

        public Diagnostic ToDiagnostic()
        {
            var start =
                diagnostic["range"]?["start"]
                ?? throw new ToolErrorException(
                    ToolError.InvalidLspData("Roslyn returned a diagnostic without a range.")
                );
            return new Diagnostic(
                OneBased(start),
                diagnostic.Severity(),
                diagnostic["code"] is JsonValue code ? code.ToString() : null,
                diagnostic["message"]?.GetValue<string>() ?? string.Empty
            );
        }
    }

    private static SymbolItem Symbol(JsonNode symbol, WorkspacePaths paths)
    {
        var name = symbol["name"]?.GetValue<string>() ?? string.Empty;
        var range = symbol["selectionRange"] ?? symbol["location"]?["range"] ?? symbol["range"];
        var uri = (symbol["location"]?["uri"] ?? symbol["uri"])?.GetValue<string>();
        return new SymbolItem(
            name,
            symbol["kind"]?.GetValue<int>() is { } kind
            && LspSymbolKinds.TryGetName(kind, out var kindName)
                ? kindName
                : null
        )
        {
            // Roslyn often repeats the name as the detail
            Detail = Distinct(symbol["detail"], name),
            Container = Distinct(symbol["containerName"], name),
            File = uri is null ? null : LocationFile(uri, paths),
            Position = range?["start"] is { } start ? OneBased(start) : null,
        };
    }

    private static List<SymbolItem> LimitSymbols(
        JsonArray symbols,
        WorkspacePaths paths,
        ref int remaining
    )
    {
        var items = new List<SymbolItem>();
        foreach (var symbol in symbols.OfType<JsonObject>())
        {
            if (remaining == 0)
            {
                break;
            }

            remaining--;
            var item = Symbol(symbol, paths);
            // A document symbol range covers the whole declaration, while selectionRange is its name
            if (symbol["selectionRange"] is not null && symbol["range"]?["end"] is { } end)
            {
                item = item with { End = OneBased(end) };
            }

            if (symbol["children"] is JsonArray { Count: > 0 } children)
            {
                item = item with { Children = LimitSymbols(children, paths, ref remaining) };
            }

            items.Add(item);
        }

        return items;
    }

    private static int CountSymbols(JsonArray symbols) =>
        symbols.Sum(symbol =>
            1 + (symbol?["children"] is JsonArray children ? CountSymbols(children) : 0)
        );

    private static string? Distinct(JsonNode? value, string name) =>
        value?.GetValue<string>() is { Length: > 0 } text
        && !string.Equals(text, name, StringComparison.Ordinal)
            ? text
            : null;

    private static SourcePosition OneBased(JsonNode position) =>
        new(position["line"]!.GetValue<int>() + 1, position["character"]!.GetValue<int>() + 1);

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
