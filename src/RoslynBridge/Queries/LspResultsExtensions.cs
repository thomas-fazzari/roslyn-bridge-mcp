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
        public SymbolList ToSymbolList(WorkspacePaths paths, int limit, int offset = 0)
        {
            var symbols = result as JsonArray ?? [];
            var skipped = offset;
            var remaining = limit;
            var total = CountSymbols(symbols);
            return new SymbolList(
                PageSymbols(symbols, paths, ref skipped, ref remaining),
                total,
                LspRequest.NextOffset(offset, limit, total)
            );
        }

        /// <summary>
        /// Converts call or type hierarchy results, in file and position order. Call results also list their call sites.
        /// </summary>
        public SymbolList ToRelatedSymbols(WorkspacePaths paths, int limit, int offset = 0)
        {
            var related = result as JsonArray ?? [];
            var items = related
                .Select(entry =>
                    Symbol(entry!["from"] ?? entry["to"] ?? entry, paths) with
                    {
                        Calls = entry["fromRanges"] is JsonArray ranges
                            ? [.. ranges.Select(range => OneBased(range!["start"]!))]
                            : [],
                    }
                )
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
        )
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
                .Skip(offset)
                .Take(limit)
                .GroupBy(location => location.File, StringComparer.Ordinal)
                .Select(group => new FileLocations(
                    group.Key,
                    [.. group.Select(location => location.Position)]
                ))
                .ToList();
            return new Locations(
                files,
                found.Count,
                LspRequest.NextOffset(offset, limit, found.Count),
                generated
            );
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
                diagnostic.Code(),
                diagnostic["message"]?.GetValue<string>() ?? string.Empty
            );
        }

        public string? Code() => diagnostic["code"] is JsonValue code ? code.ToString() : null;
    }

    private static SymbolItem Symbol(JsonNode symbol, WorkspacePaths paths)
    {
        var name = symbol["name"]?.GetValue<string>() ?? string.Empty;
        var range = symbol["selectionRange"] ?? symbol["location"]?["range"] ?? symbol["range"];
        var uri = (symbol["location"]?["uri"] ?? symbol["uri"])?.GetValue<string>();

        var kind = symbol["kind"]?.GetValue<int>();
        // A call hierarchy reports a ctor as a method named Type.Type(parameters)
        if (kind is LspSymbolKinds.Method && SymbolName.Parse(name) is { IsConstructor: true })
        {
            kind = LspSymbolKinds.Constructor;
        }

        return new SymbolItem(
            name,
            kind is { } value && LspSymbolKinds.TryGetName(value, out var kindName)
                ? kindName
                : null
        )
        {
            // Roslyn often repeats the name as the detail
            Detail = Distinct(symbol["detail"], name),
            Container = ContainerName(Distinct(symbol["containerName"], name)),
            File = uri is null ? null : LocationFile(uri, paths),
            Position = range?["start"] is { } start ? OneBased(start) : null,
        };
    }

    private static List<SymbolItem> PageSymbols(
        JsonArray symbols,
        WorkspacePaths paths,
        ref int skipped,
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

            if (skipped > 0)
            {
                skipped--;
                if (symbol["children"] is JsonArray listed)
                {
                    items.AddRange(PageSymbols(listed, paths, ref skipped, ref remaining));
                }

                continue;
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
                item = item with
                {
                    Children = PageSymbols(children, paths, ref skipped, ref remaining),
                };
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

    // Roslyn words some containers as "in Type", which the rendering already says
    private static string? ContainerName(string? container) =>
        container?.StartsWith("in ", StringComparison.Ordinal) is true ? container[3..] : container;

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
