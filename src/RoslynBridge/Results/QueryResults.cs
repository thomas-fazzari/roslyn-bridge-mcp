// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Results;

/// <summary>
/// The occurrences found in one file, a workspace-relative path or the URI of an external or generated file.
/// </summary>
/// <param name="Lines">The trimmed source line of each occurrence line, when context was requested.</param>
internal sealed record FileLocations(
    string File,
    IReadOnlyList<SourcePosition> Positions,
    IReadOnlyDictionary<int, string>? Lines = null
);

/// <param name="Total">Occurrences before paging.</param>
/// <param name="NextOffset">The offset of the next page, or null on the last page.</param>
/// <param name="Generated">Source-generated occurrences left out.</param>
internal sealed record Locations(
    IReadOnlyList<FileLocations> Files,
    int Total,
    int? NextOffset,
    int Generated
) : ToolResult;

/// <param name="Total">Symbols before paging, nested ones included.</param>
/// <param name="NextOffset">The offset of the next page, or null on the last page.</param>
/// <param name="Accesses">Property, field and constant accesses left out of callees.</param>
internal sealed record SymbolList(
    IReadOnlyList<SymbolItem> Items,
    int Total,
    int? NextOffset,
    int Accesses = 0,
    LspAction? Hierarchy = null
) : ToolResult;

/// <summary>
/// Hover information as plain text.
/// </summary>
internal sealed record HoverText(string Text) : ToolResult;

internal sealed record Diagnostic(
    SourcePosition Start,
    DiagnosticSeverity Severity,
    string? Code,
    string Message
);

internal sealed record FileDiagnostics(string File, IReadOnlyList<Diagnostic> Diagnostics);

/// <param name="Count">Diagnostics with the code, before paging.</param>
/// <param name="Example">The first diagnostic with the code, in file and position order.</param>
internal sealed record CodeSummary(int Count, string File, Diagnostic Example);

/// <param name="Codes">One entry per diagnostic code in a summary, which replaces the files.</param>
/// <param name="FilesWithDiagnostics">Files with diagnostics at the selected severity, before paging.</param>
/// <param name="Total">Diagnostics at the selected severity, before paging.</param>
/// <param name="BelowSeverity">Diagnostics left out by the severity filter.</param>
/// <param name="NextOffset">The offset of the next page of diagnostics, or of codes in a summary, or null on the last page.</param>
/// <param name="MiscellaneousFiles">Files outside every project, which get no semantic diagnostics.</param>
/// <param name="CodeCount">Codes before paging, counted in a summary only.</param>
/// <param name="FilteredByIds">Whether the counts cover only the requested codes.</param>
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

/// <summary>
/// The saved text of one declaration, with its documentation comment.
/// </summary>
internal sealed record SourcePart(SymbolItem Symbol, int FirstLine, int LastLine, string Text);

/// <param name="OmittedLines">Declaration lines left out to fit the response.</param>
internal sealed record SourceParts(IReadOnlyList<SourcePart> Parts, int OmittedLines) : ToolResult;

/// <param name="Members">Members with names relative to the type.</param>
/// <param name="NextOffset">The offset of the next page, or null on the last page.</param>
internal sealed record TypeMembers(
    SymbolItem Type,
    IReadOnlyList<SymbolItem> Members,
    int Total,
    int? NextOffset
) : ToolResult;

/// <param name="Label">What produced the section, which a client can request alone.</param>
/// <param name="Result">The section's result, or the error that kept it from having one.</param>
internal sealed record ReportSection(string Label, Result<ToolResult> Result);

/// <summary>
/// Independent navigations reported together, such as the impact of one declaration or a bulk call.
/// </summary>
internal sealed record SectionsReport(IReadOnlyList<ReportSection> Sections) : ToolResult;
