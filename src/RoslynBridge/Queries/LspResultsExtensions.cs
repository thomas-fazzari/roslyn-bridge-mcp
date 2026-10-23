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
                .. (result as JsonArray ?? [])
                    .Where(highlight => highlight?["kind"]?.GetValue<int>() == kind)
                    .Select(highlight =>
                        OneBased(
                            highlight?["range"]?["start"]
                                ?? throw new ToolErrorException(
                                    ToolError.InvalidLspData(
                                        "Roslyn returned a highlight without a position."
                                    )
                                )
                        )
                    ),
            ];
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

    /// <param name="parent">The name of the document symbol that contains this one, if any.</param>
    private static SymbolItem Symbol(JsonNode symbol, WorkspacePaths paths, string? parent = null)
    {
        var name = symbol["name"]?.GetValue<string>() ?? string.Empty;
        var range = symbol["selectionRange"] ?? symbol["location"]?["range"] ?? symbol["range"];
        var uri = (symbol["location"]?["uri"] ?? symbol["uri"])?.GetValue<string>();
        var reported = symbol["kind"]?.GetValue<int>();
        var kind =
            Qualified(name, parent ?? ContainingType(symbol["containerName"]))
                ?.CorrectedKind(reported)
            ?? reported;

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
        ref int remaining,
        string? parent = null
    )
    {
        var items = new List<SymbolItem>();
        foreach (var symbol in symbols.OfType<JsonObject>())
        {
            if (remaining == 0)
            {
                break;
            }

            var name = symbol["name"]?.GetValue<string>();
            if (skipped > 0)
            {
                skipped--;
                if (symbol["children"] is JsonArray listed)
                {
                    items.AddRange(PageSymbols(listed, paths, ref skipped, ref remaining, name));
                }

                continue;
            }

            remaining--;
            var item = Symbol(symbol, paths, parent);
            // A document symbol range covers the whole declaration, while selectionRange is its name
            if (symbol["selectionRange"] is not null && symbol["range"]?["end"] is { } end)
            {
                item = item with { End = OneBased(end) };
            }

            if (symbol["children"] is JsonArray { Count: > 0 } children)
            {
                item = item with
                {
                    Children = PageSymbols(children, paths, ref skipped, ref remaining, name),
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
    private static string? ContainingType(JsonNode? container)
    {
        var text = container?.GetValue<string>();
        if (text?.StartsWith("in ", StringComparison.Ordinal) is not true)
        {
            return null;
        }

        var project = text.IndexOf(" (project ", StringComparison.Ordinal);
        return project < 0 ? text[3..] : text[3..project];
    }

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

/// <param name="Positions">The distinct locations, in path and position order.</param>
/// <param name="Generated">Source-generated locations left out of <paramref name="Positions"/>.</param>
internal sealed record FoundLocations(
    IReadOnlyList<(string File, SourcePosition Position)> Positions,
    int Generated
);
