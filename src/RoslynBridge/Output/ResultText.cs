// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynBridge.Requests;
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
    internal const string ResolvedFilePrefix = "resolved file: ";

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
            case SectionsReport report:
                AppendSections(text, report, budget, heading: "##");
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

    /// <summary>
    /// Returns the lines that start a response whose file names were completed to workspace-relative paths.
    /// </summary>
    public static string ResolvedFiles(IEnumerable<string> files) =>
        string.Concat(files.Select(file => $"{ResolvedFilePrefix}{file}\n"));

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
    private static void AppendSections(
        StringBuilder text,
        SectionsReport report,
        int budget,
        string heading
    )
    {
        var blocks = report
            .Sections.Select(section => SectionBlock(section, budget, heading))
            .ToArray();
        var sizes = blocks.Select(block => block.Length).ToArray();
        var omitted = new SortedSet<int>();

        while (
            omitted.Count < blocks.Length
            && blocks.Sum(block => block.Length) + OmissionNote(report, sizes, omitted).Length
                > budget
        )
        {
            var largest = Array.IndexOf(blocks, blocks.MaxBy(block => block.Length));
            blocks[largest] = string.Empty;
            omitted.Add(largest);
        }

        text.AppendJoin(string.Empty, blocks).Append(OmissionNote(report, sizes, omitted));
    }

    private static string SectionBlock(ReportSection section, int budget, string heading)
    {
        var header = $"{heading} {section.Label}";
        if (section.Result.Error is { } error)
        {
            return $"{header}\n{Error(error, rpcCode: null)}\n";
        }

        switch (section.Result.Value)
        {
            case Locations { Total: 0 } or SymbolList { Total: 0 }:
                return $"{header}: none\n";
            case SectionsReport nested:
                var inner = new StringBuilder();
                AppendSections(inner, nested, budget, heading + "#");
                return $"{header}\n{Finish(inner)}\n";
            default:
                return $"{header}\n{Render(section.Result.Value, budget)}\n";
        }
    }

    private static string OmissionNote(SectionsReport report, int[] sizes, SortedSet<int> omitted)
    {
        if (omitted.Count == 0)
        {
            return string.Empty;
        }

        return new StringBuilder("omitted to fit: ")
            .AppendJoin(
                ", ",
                omitted.Select(index =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{report.Sections[index].Label} ({sizes[index]:N0} chars)"
                    )
                )
            )
            .Append(". Request them alone or in a smaller batch.\n")
            .ToString();
    }

    private static void AppendStatus(StringBuilder text, SessionStatus status)
    {
        text.Append("running: ")
            .Append(status.Running ? "true" : "false")
            .Append("\nloading: ")
            .Append(status.Loading ? "true" : "false")
            .Append('\n');
        if (status.Progress is { } progress)
        {
            text.Append("progress: ").Append(progress).Append('\n');
        }

        text.Append("workspace: ").Append(status.Workspace).Append('\n');
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

        foreach (var summary in report.Codes)
        {
            var example = summary.Example;
            if (example.Code is { } code)
            {
                text.Append(code).Append(' ');
            }

            text.Append(CultureInfo.InvariantCulture, $"x{summary.Count} ")
                .Append(example.Severity.ToString().ToLowerInvariant())
                .Append(' ')
                .Append(summary.File)
                .Append(':')
                .Append(Position(example.Start))
                .Append(' ')
                .Append(OneLine(example.Message))
                .Append('\n');
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

        if (report.FilteredByIds)
        {
            text.Append(", filtered by ids");
        }

        text.Append('\n');
        var (total, noun) =
            report.CodeCount > 0 ? (report.CodeCount, "codes") : (report.Total, "diagnostics");

        AppendTruncation(text, total, noun, report.NextOffset);

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

        if (symbols is { Total: 0, Hierarchy: { } hierarchy })
        {
            AppendNoRelated(text, hierarchy);
        }
        else
        {
            AppendPageEnd(
                text,
                symbols.Total,
                symbols.Items.Count == 0,
                symbols.NextOffset,
                "symbols"
            );
        }

        if (symbols.Accesses > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{symbols.Accesses} property, field or constant accesses omitted\n"
            );
        }
    }

    private static void AppendNoRelated(StringBuilder text, LspAction hierarchy)
    {
        text.Append("no ").Append(hierarchy.Name).Append('\n');
        if (hierarchy is LspAction.Callers)
        {
            text.Append("reflection or code outside the workspace may still call the member\n");
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

        if (locations is { Total: 0, Access: { } access })
        {
            text.Append("no ").Append(access.Name).Append('\n');
        }
        else
        {
            AppendPageEnd(
                text,
                locations.Total,
                locations.Files.Count == 0,
                locations.NextOffset,
                "locations"
            );
        }

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
                $"truncated: {source.OmittedLines} lines in declarations too large to show. Use members, then source on one member.\n"
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

        AppendPageEnd(
            text,
            members.Total,
            members.Members.Count == 0,
            members.NextOffset,
            "members"
        );
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

    // an offset past the last result leaves the page empty, which must not read as no results
    private static void AppendPageEnd(
        StringBuilder text,
        int total,
        bool pageEmpty,
        int? nextOffset,
        string noun
    )
    {
        if (total > 0 && pageEmpty)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"offset past the end: {total} {noun} in total\n"
            );
            return;
        }

        AppendEmptyOrTruncation(text, total, noun, nextOffset);
    }

    private static void AppendEmptyOrTruncation(
        StringBuilder text,
        int total,
        string noun,
        int? nextOffset,
        bool limited = false
    )
    {
        if (total == 0)
        {
            text.Append("no ").Append(noun).Append('\n');
            return;
        }

        AppendTruncation(text, total, noun, nextOffset, limited);
    }

    /// <param name="nextOffset">The offset of the next page, which a truncated paged result always has.</param>
    /// <param name="limited">Whether a result without pages left items out.</param>
    private static void AppendTruncation(
        StringBuilder text,
        int total,
        string noun,
        int? nextOffset,
        bool limited = false
    )
    {
        if (nextOffset is null && !limited)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"truncated: {total} {noun} in total");
        if (nextOffset is { } next)
        {
            text.Append(CultureInfo.InvariantCulture, $", next offset: {next}");
        }

        text.Append(". Raise limit or narrow the query.\n");
    }

    private static string Position(SourcePosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Line}:{position.Character}");

    private static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Finish(StringBuilder text) => text.ToString().TrimEnd('\n');
}
