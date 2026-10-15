// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynBridge.Queries;
using RoslynBridge.Requests;

namespace RoslynBridge.Output;

/// <summary>
/// Renders tool results as compact text lines, the only content the tools return.
/// Positions are one-based <c>line:character</c> pairs and paths are relative to the workspace.
/// A line that names a symbol starts with its location and ends with the name, which can be sent back unchanged.
/// </summary>
internal static partial class ResultText
{
    internal const int MaximumResponseCharacters = 32_000;

    private const string Indent = "  ";

    private static readonly JsonWriterOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="budget">Characters a preview or receipt may use. They drop trailing files to fit.</param>
    public static string Render(LspAction action, ToolResult? result, bool application, int budget)
    {
        var text = new StringBuilder();
        switch (result)
        {
            case HoverText hover:
                return hover.Text;
            case Locations locations:
                AppendLocations(text, locations);
                return Finish(text);
            case SymbolList symbols:
                AppendSymbols(text, symbols);
                return Finish(text);
            case JsonResult { Value: JsonObject receipt } when application:
                AppendReceipt(text, receipt, budget);
                return Finish(text);
            case JsonResult:
                break;
            default:
                throw new InvalidOperationException($"No renderer for {result?.GetType().Name}.");
        }

        var json = ((JsonResult)result).Value;
        switch (action)
        {
            case LspAction.Status or LspAction.Reload:
                AppendFields(text, (JsonObject)json!);
                break;
            case LspAction.Diagnostics:
                AppendDiagnostics(text, (JsonObject)json!);
                break;
            case LspAction.Source:
                AppendSource(text, (JsonObject)json!);
                break;
            case LspAction.Members:
                AppendMembers(text, (JsonObject)json!);
                break;
            case LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions:
                AppendChange(text, (JsonObject)json!, budget);
                break;
            default:
                text.Append(Json(json));
                break;
        }

        return Finish(text);
    }

    public static string Error(ToolError error, int? rpcCode)
    {
        var text = new StringBuilder("error ").Append(error.Code);
        if (rpcCode is { } code)
        {
            text.Append(CultureInfo.InvariantCulture, $" (LSP {code})");
        }

        text.Append(": ").Append(OneLine(error.Message)).Append('\n');
        foreach (var candidate in error.Candidates ?? [])
        {
            AppendSymbol(text, candidate, indent: string.Empty);
        }

        return Finish(text);
    }

    public static string Json(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _jsonOptions))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void AppendFields(StringBuilder text, JsonObject result)
    {
        foreach (var (name, value) in result)
        {
            if (value is not null)
            {
                text.Append(name).Append(": ").Append(Scalar(value)).Append('\n');
            }
        }
    }

    private static void AppendDiagnostics(StringBuilder text, JsonObject result)
    {
        foreach (var file in Items(result, "diagnostics"))
        {
            text.Append(file["file"]!.GetValue<string>()).Append('\n');
            foreach (var diagnostic in file["diagnostics"]!.AsArray().OfType<JsonObject>())
            {
                text.Append(Indent)
                    .Append(Position(diagnostic["line"]!, diagnostic["character"]!))
                    .Append(' ')
                    .Append(diagnostic["severity"]!.GetValue<string>());
                if (diagnostic["code"] is { } code)
                {
                    text.Append(' ').Append(Scalar(code));
                }

                text.Append(' ')
                    .Append(OneLine(diagnostic["message"]?.GetValue<string>() ?? string.Empty))
                    .Append('\n');
            }
        }

        var total = Int(result, "total");
        var summary = new StringBuilder(
            Count(Int(result, "filesChecked"), "file", "files") + " checked, "
        );
        summary.Append(
            total == 0
                ? "no diagnostics"
                : $"{Count(total, "diagnostic", "diagnostics")} in {Count(Int(result, "filesWithDiagnostics"), "file", "files")}"
        );
        if (Int(result, "belowSeverity") is > 0 and var below)
        {
            summary.Append(CultureInfo.InvariantCulture, $", {below} below the severity");
        }

        text.Append(summary).Append('\n');
        AppendTruncation(
            text,
            Int(result, "total"),
            result["truncated"]?.GetValue<bool>() is true,
            "diagnostics"
        );
        if (result["miscellaneousFiles"] is JsonArray { Count: > 0 } miscellaneous)
        {
            text.Append("incomplete: not in a project, no semantic diagnostics:");
            foreach (var file in miscellaneous)
            {
                text.Append(' ').Append(file!.GetValue<string>());
            }

            text.Append('\n');
        }
    }

    private static void AppendSymbols(StringBuilder text, SymbolList symbols)
    {
        foreach (var symbol in symbols.Items)
        {
            AppendSymbolTree(text, symbol, indent: string.Empty);
        }

        AppendEmptyOrTruncation(text, symbols.Total, symbols.Truncated, "symbols");
        if (symbols.Accesses > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{symbols.Accesses} property, field or constant accesses omitted\n"
            );
        }
    }

    // A symbol line, then its call sites or source lines, then its members indented below it
    private static void AppendSymbolTree(StringBuilder text, SymbolItem symbol, string indent)
    {
        AppendSymbol(text, symbol, indent);
        if (symbol.Lines is { } lines)
        {
            AppendSourceLines(text, LinePositions(symbol), lines);
        }
        else if (symbol.Calls.Count > 0)
        {
            text.Append(Indent)
                .Append("calls ")
                .AppendJoin(' ', symbol.Calls.Select(Position))
                .Append('\n');
        }

        foreach (var child in symbol.Children)
        {
            AppendSymbolTree(text, child, indent + Indent);
        }
    }

    // Source lines belong to the call sites when there are some, and to the declaration otherwise
    private static IEnumerable<SourcePosition> LinePositions(SymbolItem symbol)
    {
        if (symbol.Calls.Count > 0)
        {
            return symbol.Calls;
        }

        return symbol.Position is { } position ? [position] : [];
    }

    private static void AppendLocations(StringBuilder text, Locations locations)
    {
        foreach (var file in locations.Files)
        {
            text.Append(file.File);
            if (file.Lines is { } lines)
            {
                text.Append('\n');
                AppendSourceLines(text, file.Positions, lines);
                continue;
            }

            text.Append(' ').AppendJoin(' ', file.Positions.Select(Position)).Append('\n');
        }

        AppendEmptyOrTruncation(text, locations.Total, locations.Truncated, "locations");
        if (locations.Generated > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"generated code: {locations.Generated} more omitted. Use lsp request to list them.\n"
            );
        }
    }

    // A header per declaration, file:first-last kind name, then its saved text
    private static void AppendSource(StringBuilder text, JsonObject result)
    {
        foreach (var part in Items(result))
        {
            var lines = part["lines"]!;
            text.Append(part["file"]!.GetValue<string>())
                .Append(
                    CultureInfo.InvariantCulture,
                    $":{lines[0]!.GetValue<int>()}-{lines[1]!.GetValue<int>()} "
                )
                .Append(part["kind"]?.GetValue<string>())
                .Append(' ')
                .Append(part["symbol"]!.GetValue<string>())
                .Append('\n')
                .Append(part["text"]!.GetValue<string>());
        }

        if (Int(result, "omittedLines") is > 0 and var omitted)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {omitted} more lines. Use members, then source on one member.\n"
            );
        }
    }

    // The type once, then its members grouped by file, with names relative to the type
    private static void AppendMembers(StringBuilder text, JsonObject result)
    {
        text.Append(result["kind"]?.GetValue<string>())
            .Append(' ')
            .Append(result["symbol"]!.GetValue<string>())
            .Append('\n');
        string? file = null;
        foreach (var member in Items(result))
        {
            var memberFile = member["file"]!.GetValue<string>();
            if (!string.Equals(memberFile, file, StringComparison.Ordinal))
            {
                file = memberFile;
                text.Append(file).Append('\n');
            }

            text.Append(Indent)
                .Append(Position(member["position"]!))
                .Append(' ')
                .Append(member["kind"]?.GetValue<string>())
                .Append(' ')
                .Append(member["symbol"]!.GetValue<string>())
                .Append('\n');
        }

        AppendEmptyOrTruncation(text, result, "members");
    }

    private static void AppendSourceLines(
        StringBuilder text,
        IEnumerable<SourcePosition> positions,
        IReadOnlyDictionary<int, string> lines
    )
    {
        foreach (var position in positions)
        {
            text.Append(Indent).Append(Position(position));
            if (lines.TryGetValue(position.Line, out var source) && source.Length > 0)
            {
                text.Append(' ').Append(source);
            }

            text.Append('\n');
        }
    }

    // location kind name, with the detail and container when Roslyn reports them
    private static void AppendSymbol(StringBuilder text, SymbolItem symbol, string indent)
    {
        text.Append(indent);
        if (Location(symbol) is { } location)
        {
            text.Append(location).Append(' ');
        }

        if (symbol.Kind is { } kind)
        {
            text.Append(kind).Append(' ');
        }

        text.Append(symbol.Name);
        if (symbol.Detail is { } detail)
        {
            text.Append(" : ").Append(detail);
        }

        if (symbol.Container is { } container)
        {
            text.Append(" in ").Append(container);
        }

        text.Append('\n');
    }

    private static string? Location(SymbolItem symbol)
    {
        var position = symbol.Position is { } first ? Position(first) : null;
        if (position is not null && symbol.End is { } end)
        {
            position += "-" + Position(end);
        }

        return (symbol.File, position) switch
        {
            ({ } file, { } start) => $"{file}:{start}",
            ({ } file, null) => file,
            (null, var start) => start,
        };
    }

    private static void AppendEmptyOrTruncation(
        StringBuilder text,
        JsonObject result,
        string noun
    ) =>
        AppendEmptyOrTruncation(
            text,
            Int(result, "total"),
            result["truncated"]?.GetValue<bool>() is true,
            noun
        );

    private static void AppendEmptyOrTruncation(
        StringBuilder text,
        int total,
        bool truncated,
        string noun
    )
    {
        if (total == 0)
        {
            text.Append("no ").Append(noun).Append('\n');
            return;
        }

        AppendTruncation(text, total, truncated, noun);
    }

    private static void AppendTruncation(StringBuilder text, int total, bool truncated, string noun)
    {
        if (truncated)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {total} {noun} in total. Raise limit or narrow the query.\n"
            );
        }
    }

    private static IEnumerable<JsonObject> Items(JsonObject result, string name = "items") =>
        (result[name] as JsonArray ?? []).OfType<JsonObject>();

    private static string Position(JsonNode position) => Position(position[0]!, position[1]!);

    private static string Position(SourcePosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Line}:{position.Character}");

    private static string Position(JsonNode line, JsonNode character) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{line.GetValue<int>()}:{character.GetValue<int>()}"
        );

    private static int Int(JsonObject result, string name) => result[name]?.GetValue<int>() ?? 0;

    private static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    private static string Scalar(JsonNode value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : Json(value);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Finish(StringBuilder text) => text.ToString().TrimEnd('\n');
}
