// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynBridge.Results;

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
    public static string Render(ToolResult? result, int budget)
    {
        var text = new StringBuilder();
        switch (result)
        {
            case HoverText hover:
                return hover.Text;
            case JsonResult json:
                return Json(json.Value);
            case Locations locations:
                AppendLocations(text, locations);
                break;
            case SymbolList symbols:
                AppendSymbols(text, symbols);
                break;
            case DiagnosticReport report:
                AppendDiagnostics(text, report);
                break;
            case SourceParts source:
                AppendSource(text, source);
                break;
            case TypeMembers members:
                AppendMembers(text, members);
                break;
            case ImpactReport impact:
                AppendImpact(text, impact, budget);
                break;
            case SessionStatus status:
                AppendStatus(text, status);
                break;
            case ActionListing listing:
                AppendActions(text, listing);
                break;
            case ProposalPreview preview:
                AppendPreview(text, preview, budget);
                break;
            case EditReceipt receipt:
                AppendReceipt(text, receipt, budget);
                break;
            default:
                throw new InvalidOperationException($"No renderer for {result?.GetType().Name}.");
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

    // A block per section, dropping the largest ones when they do not all fit
    private static void AppendImpact(StringBuilder text, ImpactReport impact, int budget)
    {
        var blocks = impact
            .Sections.Select(section =>
                section.Result is Locations { Total: 0 } or SymbolList { Total: 0 }
                    ? $"# {section.Name}: none\n"
                    : $"# {section.Name}\n{Render(section.Result, budget)}\n"
            )
            .ToArray();
        var omitted = new SortedSet<int>();

        while (blocks.Sum(block => block.Length) > budget - ClosingReserve)
        {
            var largest = Array.IndexOf(blocks, blocks.MaxBy(block => block.Length));
            blocks[largest] = string.Empty;
            omitted.Add(largest);
        }

        text.AppendJoin(string.Empty, blocks);
        if (omitted.Count > 0)
        {
            text.Append("omitted to fit: ")
                .AppendJoin(", ", omitted.Select(index => impact.Sections[index].Name))
                .Append(". Request each alone.\n");
        }
    }

    private static void AppendStatus(StringBuilder text, SessionStatus status)
    {
        text.Append("running: ")
            .Append(status.Running ? "true" : "false")
            .Append("\nloading: ")
            .Append(status.Loading ? "true" : "false")
            .Append("\nworkspace: ")
            .Append(status.Workspace)
            .Append('\n');
        if (status.Server is { } server)
        {
            text.Append("server: ").Append(server).Append('\n');
        }
    }

    private static void AppendDiagnostics(StringBuilder text, DiagnosticReport report)
    {
        foreach (var file in report.Files)
        {
            text.Append(file.File).Append('\n');
            foreach (var diagnostic in file.Diagnostics)
            {
                text.Append(Indent)
                    .Append(Position(diagnostic.Start))
                    .Append(' ')
                    .Append(diagnostic.Severity.ToString().ToLowerInvariant());
                if (diagnostic.Code is { } code)
                {
                    text.Append(' ').Append(code);
                }

                text.Append(' ').Append(OneLine(diagnostic.Message)).Append('\n');
            }
        }

        text.Append(Count(report.FilesChecked, "file", "files"))
            .Append(" checked, ")
            .Append(
                report.Total == 0
                    ? "no diagnostics"
                    : $"{Count(report.Total, "diagnostic", "diagnostics")} in {Count(report.FilesWithDiagnostics, "file", "files")}"
            );
        if (report.BelowSeverity > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $", {report.BelowSeverity} below the severity"
            );
        }

        text.Append('\n');
        AppendTruncation(text, report.Total, report.Truncated, "diagnostics");
        if (report.MiscellaneousFiles.Count > 0)
        {
            text.Append("incomplete: not in a project, no semantic diagnostics: ")
                .AppendJoin(' ', report.MiscellaneousFiles)
                .Append('\n');
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
    private static void AppendSource(StringBuilder text, SourceParts source)
    {
        foreach (var part in source.Parts)
        {
            text.Append(part.Symbol.File)
                .Append(CultureInfo.InvariantCulture, $":{part.FirstLine}-{part.LastLine} ")
                .Append(part.Symbol.Kind)
                .Append(' ')
                .Append(part.Symbol.Name)
                .Append('\n')
                .Append(part.Text);
        }

        if (source.OmittedLines > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {source.OmittedLines} more lines. Use members, then source on one member.\n"
            );
        }
    }

    // The type once, then its members grouped by file, with names relative to the type
    private static void AppendMembers(StringBuilder text, TypeMembers members)
    {
        text.Append(members.Type.Kind).Append(' ').Append(members.Type.Name).Append('\n');
        string? file = null;
        foreach (var member in members.Members)
        {
            if (!string.Equals(member.File, file, StringComparison.Ordinal))
            {
                file = member.File;
                text.Append(file).Append('\n');
            }

            text.Append(Indent)
                .Append(Position(member.Position!.Value))
                .Append(' ')
                .Append(member.Kind)
                .Append(' ')
                .Append(member.Name)
                .Append('\n');
        }

        AppendEmptyOrTruncation(text, members.Total, members.Truncated, "members");
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

    private static string Position(SourcePosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Line}:{position.Character}");

    private static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Finish(StringBuilder text) => text.ToString().TrimEnd('\n');
}
