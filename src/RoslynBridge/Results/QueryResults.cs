// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Results;

/// <param name="File">A workspace-relative path, or the URI of an external or generated file.</param>
internal sealed record FileLocations(
    string File,
    IReadOnlyList<SourcePosition> Positions,
    IReadOnlyDictionary<int, string>? Lines = null
);

/// <param name="Generated">Source-generated occurrences left out.</param>
/// <param name="Access">Reads or Writes when the references are filtered to that access.</param>
internal sealed record Locations(
    IReadOnlyList<FileLocations> Files,
    int Total,
    int? NextOffset,
    int Generated,
    LspAction? Access = null
) : ToolResult;

/// <param name="Total">Nested symbols included.</param>
/// <param name="Accesses">Property, field and constant accesses left out of callees.</param>
internal sealed record SymbolList(
    IReadOnlyList<SymbolItem> Items,
    int Total,
    int? NextOffset,
    int Accesses = 0,
    LspAction? Hierarchy = null
) : ToolResult;

internal sealed record HoverText(string Text) : ToolResult;

internal sealed record Diagnostic(
    SourcePosition Start,
    DiagnosticSeverity Severity,
    string? Code,
    string Message
);

internal sealed record FileDiagnostics(string File, IReadOnlyList<Diagnostic> Diagnostics);

/// <param name="Example">The first diagnostic with the code, in file and position order.</param>
internal sealed record CodeSummary(int Count, string File, Diagnostic Example);

/// <param name="Codes">In a summary, one entry per code. They replace the files.</param>
/// <param name="Total">Diagnostics at the selected severity, before paging.</param>
/// <param name="BelowSeverity">Diagnostics left out by the severity filter.</param>
/// <param name="MiscellaneousFiles">Files outside every project. They get no semantic diagnostics.</param>
/// <param name="CodeCount">Codes before paging, counted in a summary only.</param>
internal sealed record DiagnosticReport(
    int FilesChecked,
    IReadOnlyList<FileDiagnostics> Files,
    IReadOnlyList<CodeSummary> Codes,
    int FilesWithDiagnostics,
    int Total,
    int BelowSeverity,
    int? NextOffset,
    IReadOnlyList<string> MiscellaneousFiles,
    int CodeCount,
    bool FilteredByIds
) : ToolResult;

internal sealed record SourcePart(SymbolItem Symbol, int FirstLine, int LastLine, string Text);

/// <param name="OmittedLines">Declaration lines left out to fit the response.</param>
internal sealed record SourceParts(IReadOnlyList<SourcePart> Parts, int OmittedLines) : ToolResult;

/// <param name="Members">Names are relative to the type.</param>
internal sealed record TypeMembers(
    SymbolItem Type,
    IReadOnlyList<SymbolItem> Members,
    int Total,
    int? NextOffset
) : ToolResult;

/// <param name="Result">The error when the section could not produce a result.</param>
internal sealed record ReportSection(string Label, Result<ToolResult> Result);

internal sealed record SectionsReport(IReadOnlyList<ReportSection> Sections) : ToolResult;
