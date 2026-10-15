// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

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

/// <param name="Total">Occurrences before limiting.</param>
/// <param name="Generated">Source-generated occurrences left out.</param>
internal sealed record Locations(
    IReadOnlyList<FileLocations> Files,
    int Total,
    bool Truncated,
    int Generated
) : ToolResult;

/// <param name="Total">Symbols before limiting, nested ones included.</param>
/// <param name="Accesses">Property, field and constant accesses left out of callees.</param>
internal sealed record SymbolList(
    IReadOnlyList<SymbolItem> Items,
    int Total,
    bool Truncated,
    int Accesses = 0
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

/// <param name="FilesWithDiagnostics">Files with diagnostics at the selected severity, before limiting.</param>
/// <param name="Total">Diagnostics at the selected severity, before limiting.</param>
/// <param name="BelowSeverity">Diagnostics left out by the severity filter.</param>
/// <param name="MiscellaneousFiles">Files outside every project, which get no semantic diagnostics.</param>
internal sealed record DiagnosticReport(
    int FilesChecked,
    IReadOnlyList<FileDiagnostics> Files,
    int FilesWithDiagnostics,
    int Total,
    int BelowSeverity,
    bool Truncated,
    IReadOnlyList<string> MiscellaneousFiles
) : ToolResult;

/// <summary>
/// The saved text of one declaration, with its documentation comment.
/// </summary>
internal sealed record SourcePart(SymbolItem Symbol, int FirstLine, int LastLine, string Text);

/// <param name="OmittedLines">Declaration lines left out to fit the response.</param>
internal sealed record SourceParts(IReadOnlyList<SourcePart> Parts, int OmittedLines) : ToolResult;

/// <param name="Members">Members with names relative to the type.</param>
internal sealed record TypeMembers(
    SymbolItem Type,
    IReadOnlyList<SymbolItem> Members,
    int Total,
    bool Truncated
) : ToolResult;
