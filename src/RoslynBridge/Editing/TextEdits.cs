// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Editing;

internal static class TextEdits
{
    internal const int MaximumTextEditCount = 10_000;

    public static Result<string> Apply(string source, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count > MaximumTextEditCount)
        {
            return ToolError.InvalidRequest(
                $"The edit exceeds {MaximumTextEditCount} text changes."
            );
        }

        var lines = GetLines(source);
        var parseResult = edits.Select((edit, index) => Parse(edit, index, lines)).All();
        if (parseResult.IsError(out var error, out var parsed))
            return error;

        var changes = parsed
            .OrderBy(change => change.Start)
            .ThenBy(change => change.End)
            .ThenBy(change => change.Index);
        var result = new StringBuilder(source.Length);
        var offset = 0;
        foreach (var change in changes)
        {
            if (change.Start < offset || change.End < change.Start)
                return ToolError.InvalidLspData("Text edit ranges overlap or run backwards.");

            result.Append(source, offset, change.Start - offset).Append(change.Text);
            offset = change.End;
            if (result.Length > WorkspaceEditService.MaximumFileBytes)
                return ToolError.InvalidRequest("The edited document exceeds the size limit.");
        }

        result.Append(source, offset, source.Length - offset);
        return result.ToString();
    }

    internal static List<LineSpan> GetLines(string source)
    {
        var lines = new List<LineSpan>();
        var start = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (!IsLineBreak(source[index]))
                continue;

            lines.Add(new LineSpan(start, index - start));
            if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                index++;

            start = index + 1;
        }

        lines.Add(new LineSpan(start, source.Length - start));
        return lines;
    }

    // Roslyn's SourceText line breaks, which LSP positions count
    internal static bool IsLineBreak(char character) =>
        character is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    private static Result<Change> Parse(TextEdit edit, int index, List<LineSpan> lines) =>
        GetOffset(edit.Range.Start, lines) is not { } start
        || GetOffset(edit.Range.End, lines) is not { } end
            ? ToolError.InvalidLspData("The text edit position is outside the document.")
            : new Change(start, end, edit.NewText, index);

    /// <summary>
    /// Null for a position outside the document.
    /// </summary>
    private static int? GetOffset(LspPosition position, List<LineSpan> lines)
    {
        var (line, character) = (position.Line, position.Character);
        return line < 0 || line >= lines.Count || character < 0 || character > lines[line].Length
            ? null
            : lines[line].Start + character;
    }

    private sealed record Change(int Start, int End, string Text, int Index);
}

/// <summary>
/// The length excludes the line break.
/// </summary>
internal readonly record struct LineSpan(int Start, int Length);
