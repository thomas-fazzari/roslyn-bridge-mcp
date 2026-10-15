// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;

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
        public JsonObject ToCompactHover() =>
            new() { ["text"] = HoverText(result?["contents"]).Trim() };

        /// <summary>
        /// Converts document or workspace symbols to name, kind and one-based position entries.
        /// Document symbols keep their nesting in children and their declaration end.
        /// The limit applies to nested symbols too, in document order.
        /// </summary>
        public JsonObject ToCompactSymbols(WorkspacePaths paths, int limit)
        {
            var symbols = result as JsonArray ?? [];
            var remaining = limit;
            var total = CountSymbols(symbols);
            return new JsonObject
            {
                ["items"] = LimitSymbols(symbols, paths, ref remaining),
                ["total"] = total,
                ["truncated"] = total > limit,
            };
        }

        /// <summary>
        /// Converts call or type hierarchy results to symbol entries.
        /// Call results also list the one-based positions of their call sites in calls.
        /// </summary>
        public JsonObject ToCompactHierarchy(WorkspacePaths paths, int limit)
        {
            var related = result as JsonArray ?? [];
            var items = new JsonArray();
            foreach (var entry in related.Take(limit))
            {
                var symbol = CompactSymbol(entry!["from"] ?? entry["to"] ?? entry, paths);
                if (entry["fromRanges"] is JsonArray ranges)
                {
                    symbol["calls"] = new JsonArray([
                        .. ranges.Select(range => (JsonNode)OneBased(range!["start"]!)),
                    ]);
                }

                items.Add((JsonNode)symbol);
            }

            return new JsonObject
            {
                ["items"] = items,
                ["total"] = related.Count,
                ["truncated"] = related.Count > limit,
            };
        }

        /// <summary>
        /// Groups locations by file, in path and position order, without duplicates.
        /// With <paramref name="skipGenerated"/>, source-generated locations are counted in generated instead.
        /// </summary>
        public JsonObject ToCompactLocations(WorkspacePaths paths, int limit, bool skipGenerated)
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
            var found = new HashSet<(string File, int Line, int Character)>();
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

                found.Add(
                    (
                        LocationFile(uri, paths),
                        start["line"]!.GetValue<int>() + 1,
                        start["character"]!.GetValue<int>() + 1
                    )
                );
            }

            var items = new JsonArray();
            foreach (
                var group in found
                    .OrderBy(location => location.File, StringComparer.Ordinal)
                    .ThenBy(location => location.Line)
                    .ThenBy(location => location.Character)
                    .Take(limit)
                    .GroupBy(location => location.File, StringComparer.Ordinal)
            )
            {
                items.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["file"] = group.Key,
                            ["positions"] = new JsonArray([
                                .. group.Select(location =>
                                    (JsonNode)new JsonArray(location.Line, location.Character)
                                ),
                            ]),
                        }
                );
            }

            var compact = new JsonObject
            {
                ["items"] = items,
                ["total"] = found.Count,
                ["truncated"] = found.Count > limit,
            };
            if (generated > 0)
            {
                compact["generated"] = generated;
            }

            return compact;
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

        public JsonObject ToCompactDiagnostic()
        {
            var start =
                diagnostic["range"]?["start"]
                ?? throw new ToolErrorException(
                    ToolError.InvalidLspData("Roslyn returned a diagnostic without a range.")
                );
            var compact = new JsonObject
            {
                ["line"] = start["line"]!.GetValue<int>() + 1,
                ["character"] = start["character"]!.GetValue<int>() + 1,
            };
            compact["severity"] = diagnostic.Severity().ToString().ToLowerInvariant();

            compact["code"] = diagnostic["code"]?.DeepClone();
            compact["message"] = diagnostic["message"]?.DeepClone();
            return compact;
        }
    }

    private static JsonObject CompactSymbol(JsonNode symbol, WorkspacePaths paths)
    {
        var name = symbol["name"]?.GetValue<string>();
        var compact = new JsonObject { ["name"] = name };
        if (
            symbol["kind"]?.GetValue<int>() is { } kind
            && LspSymbolKinds.TryGetName(kind, out var kindName)
        )
        {
            compact["kind"] = kindName;
        }

        // Roslyn often repeats the name as the detail
        AddText(compact, "detail", symbol["detail"], name);
        AddText(compact, "container", symbol["containerName"], name);
        AddLocation(compact, symbol, paths);
        return compact;
    }

    private static JsonArray LimitSymbols(
        JsonArray symbols,
        WorkspacePaths paths,
        ref int remaining
    )
    {
        var items = new JsonArray();
        foreach (var symbol in symbols.OfType<JsonObject>())
        {
            if (remaining == 0)
            {
                break;
            }

            remaining--;
            var compact = CompactSymbol(symbol, paths);
            // A document symbol range covers the whole declaration, while selectionRange is its name
            if (symbol["selectionRange"] is not null && symbol["range"]?["end"] is { } end)
            {
                compact["end"] = OneBased(end);
            }

            if (symbol["children"] is JsonArray { Count: > 0 } children)
            {
                var limited = LimitSymbols(children, paths, ref remaining);
                if (limited.Count > 0)
                {
                    compact["children"] = limited;
                }
            }

            items.Add((JsonNode)compact);
        }

        return items;
    }

    private static int CountSymbols(JsonArray symbols) =>
        symbols.Sum(symbol =>
            1 + (symbol?["children"] is JsonArray children ? CountSymbols(children) : 0)
        );

    private static void AddText(JsonObject compact, string key, JsonNode? value, string? name)
    {
        if (
            value?.GetValue<string>() is { Length: > 0 } text
            && !string.Equals(text, name, StringComparison.Ordinal)
        )
        {
            compact[key] = text;
        }
    }

    private static void AddLocation(JsonObject compact, JsonNode symbol, WorkspacePaths paths)
    {
        if ((symbol["location"]?["uri"] ?? symbol["uri"])?.GetValue<string>() is { } uri)
        {
            compact["file"] = LocationFile(uri, paths);
        }

        var range = symbol["selectionRange"] ?? symbol["location"]?["range"] ?? symbol["range"];
        if (range?["start"] is { } start)
        {
            compact["position"] = OneBased(start);
        }
    }

    private static JsonArray OneBased(JsonNode position) =>
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
