// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Results;

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
) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var file in Files)
        {
            text.Append(file.File).Append('\n');
            foreach (var diagnostic in file.Diagnostics)
            {
                text.Append(INDENT)
                    .Append(Position(diagnostic.Start))
                    .Append(' ')
                    .Append(diagnostic.Severity.ToString().ToLowerInvariant());
                if (diagnostic.Code is { } code)
                    text.Append(' ').Append(code);

                text.Append(' ').Append(OneLine(diagnostic.Message)).Append('\n');
            }
        }

        foreach (var summary in Codes)
        {
            var example = summary.Example;
            if (example.Code is { } code)
                text.Append(code).Append(' ');

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

        text.Append(Count(FilesChecked, "file", "files"))
            .Append(" checked, ")
            .Append(
                Total == 0
                    ? "no diagnostics"
                    : $"{Count(Total, "diagnostic", "diagnostics")} in {Count(FilesWithDiagnostics, "file", "files")}"
            );
        if (BelowSeverity > 0)
            text.Append(CultureInfo.InvariantCulture, $", {BelowSeverity} below the severity");

        if (FilteredByIds)
            text.Append(", filtered by ids");

        text.Append('\n');
        var (total, noun) = CodeCount > 0 ? (CodeCount, "codes") : (Total, "diagnostics");
        AppendTruncation(text, total, noun, NextOffset);

        if (MiscellaneousFiles.Count > 0)
        {
            text.Append("incomplete: not in a project, no semantic diagnostics: ")
                .AppendJoin(' ', MiscellaneousFiles)
                .Append('\n');
        }
    }
}
