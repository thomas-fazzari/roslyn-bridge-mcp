// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Previews are measured as JSON, which is longer than the rendered text
    internal const int PreviewBudgetCharacters = MaximumResponseCharacters - 100;
    private const string Indent = "  ";

    private static readonly JsonWriterOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="budget">Characters a receipt may use. It drops trailing files to fit.</param>
    public static string Render(LspAction action, JsonNode? result, bool application, int budget)
    {
        var text = new StringBuilder();
        if (application && result is JsonObject receipt)
        {
            AppendReceipt(text, receipt, budget);
            return Finish(text);
        }

        switch (action)
        {
            case LspAction.Status or LspAction.Reload:
                AppendFields(text, (JsonObject)result!);
                break;
            case LspAction.Diagnostics:
                AppendDiagnostics(text, (JsonObject)result!);
                break;
            case LspAction.Symbols:
                AppendSymbols(text, (JsonObject)result!);
                break;
            case LspAction.Definition
            or LspAction.TypeDefinition
            or LspAction.Implementation
            or LspAction.References:
                AppendLocations(text, (JsonObject)result!);
                break;
            case LspAction.Hover:
                text.Append(result?["text"]?.GetValue<string>());
                break;
            case LspAction.Callers
            or LspAction.Callees
            or LspAction.Supertypes
            or LspAction.Subtypes:
                AppendRelated(text, (JsonObject)result!);
                break;
            case LspAction.Source:
                AppendSource(text, (JsonObject)result!);
                break;
            case LspAction.Members:
                AppendMembers(text, (JsonObject)result!);
                break;
            case LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions:
                AppendChange(text, (JsonObject)result!);
                break;
            default:
                text.Append(Json(result));
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
        foreach (var candidate in (error.Candidates ?? []).OfType<JsonObject>())
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
                    .Append(diagnostic["severity"]?.GetValue<string>() ?? "error");
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
        AppendTruncation(text, result, "diagnostics");
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

    private static void AppendSymbols(StringBuilder text, JsonObject result)
    {
        foreach (var symbol in Items(result))
        {
            AppendSymbolTree(text, symbol, indent: string.Empty);
        }

        AppendEmptyOrTruncation(text, result, "symbols");
    }

    private static void AppendSymbolTree(StringBuilder text, JsonObject symbol, string indent)
    {
        AppendSymbol(text, symbol, indent);
        foreach (var child in (symbol["children"] as JsonArray ?? []).OfType<JsonObject>())
        {
            AppendSymbolTree(text, child, indent + Indent);
        }
    }

    private static void AppendLocations(StringBuilder text, JsonObject result)
    {
        foreach (var item in Items(result))
        {
            var positions = item["positions"]!.AsArray();
            text.Append(item["file"]!.GetValue<string>());
            if (item["lines"] is JsonObject lines)
            {
                text.Append('\n');
                AppendSourceLines(text, positions, lines);
                continue;
            }

            foreach (var position in positions)
            {
                text.Append(' ').Append(Position(position!));
            }

            text.Append('\n');
        }

        AppendEmptyOrTruncation(text, result, "locations");

        if (Int(result, "generated") is > 0 and var generated)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"generated code: {generated} more omitted. Use lsp request to list them.\n"
            );
        }
    }

    private static void AppendRelated(StringBuilder text, JsonObject result)
    {
        foreach (var item in Items(result))
        {
            AppendSymbol(text, item, indent: string.Empty);
            var calls = item["calls"] as JsonArray;
            if (item["lines"] is JsonObject lines)
            {
                AppendSourceLines(
                    text,
                    calls ?? new JsonArray(item["position"]?.DeepClone()),
                    lines
                );
            }
            else if (calls is { Count: > 0 })
            {
                text.Append(Indent).Append("calls");
                foreach (var call in calls)
                {
                    text.Append(' ').Append(Position(call!));
                }

                text.Append('\n');
            }
        }

        AppendEmptyOrTruncation(text, result, "symbols");

        if (Int(result, "accesses") is > 0 and var accesses)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{accesses} property, field or constant accesses omitted\n"
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

    private static void AppendSourceLines(StringBuilder text, JsonArray positions, JsonObject lines)
    {
        foreach (var position in positions.OfType<JsonArray>())
        {
            text.Append(Indent).Append(Position(position));
            var line = position[0]!.GetValue<int>().ToString(CultureInfo.InvariantCulture);
            if (lines[line]?.GetValue<string>() is { Length: > 0 } source)
            {
                text.Append(' ').Append(source);
            }

            text.Append('\n');
        }
    }

    private static void AppendSymbol(StringBuilder text, JsonObject symbol, string indent)
    {
        text.Append(indent);
        if (Location(symbol) is { } location)
        {
            text.Append(location).Append(' ');
        }

        if (symbol["kind"]?.GetValue<string>() is { } kind)
        {
            text.Append(kind).Append(' ');
        }

        text.Append((symbol["symbol"] ?? symbol["name"])?.GetValue<string>());
        if (symbol["detail"]?.GetValue<string>() is { } detail)
        {
            text.Append(" : ").Append(detail);
        }

        if (symbol["container"]?.GetValue<string>() is { } container)
        {
            text.Append(" in ").Append(container);
        }

        text.Append('\n');
    }

    private static string? Location(JsonObject symbol)
    {
        var position = symbol["position"] is JsonArray value ? Position(value) : null;
        if (position is not null && symbol["end"] is JsonArray end)
        {
            position += "-" + Position(end);
        }

        return (symbol["file"]?.GetValue<string>(), position) switch
        {
            ({ } file, { } start) => $"{file}:{start}",
            ({ } file, null) => file,
            (null, var start) => start,
        };
    }

    private static void AppendEmptyOrTruncation(StringBuilder text, JsonObject result, string noun)
    {
        if (Int(result, "total") == 0)
        {
            text.Append("no ").Append(noun).Append('\n');
            return;
        }

        AppendTruncation(text, result, noun);
    }

    private static void AppendTruncation(StringBuilder text, JsonObject result, string noun)
    {
        if (result["truncated"]?.GetValue<bool>() is true)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {Int(result, "total")} {noun} in total. Raise limit or narrow the query.\n"
            );
        }
    }

    private static IEnumerable<JsonObject> Items(JsonObject result, string name = "items") =>
        (result[name] as JsonArray ?? []).OfType<JsonObject>();

    private static string Position(JsonNode position) => Position(position[0]!, position[1]!);

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
