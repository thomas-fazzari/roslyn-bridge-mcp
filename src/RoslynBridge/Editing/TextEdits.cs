// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using RoslynBridge.Lsp;

namespace RoslynBridge.Editing;

internal static class TextEdits
{
    internal const int MaximumTextEditCount = 10_000;

    public static string Apply(string source, TextEdit?[] edits)
    {
        if (edits.Length > MaximumTextEditCount)
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest($"The edit exceeds {MaximumTextEditCount} text changes.")
            );
        }

        var lines = GetLines(source);
        var changes = edits
            .Select((edit, index) => Parse(edit, index, lines))
            .OrderBy(change => change.Start)
            .ThenBy(change => change.End)
            .ThenBy(change => change.Index);
        var result = new StringBuilder(source.Length);
        var offset = 0;
        foreach (var change in changes)
        {
            if (change.Start < offset || change.End < change.Start)
            {
                throw new ToolErrorException(
                    ToolError.InvalidLspData("Text edit ranges overlap or run backwards.")
                );
            }

            result.Append(source, offset, change.Start - offset).Append(change.Text);
            offset = change.End;
            if (result.Length > WorkspaceEditService.MaximumFileBytes)
            {
                throw new ToolErrorException(
                    ToolError.InvalidRequest("The edited document exceeds the size limit.")
                );
            }
        }

        result.Append(source, offset, source.Length - offset);
        return result.ToString();
    }

    private static Change Parse(TextEdit? edit, int index, List<LineSpan> lines)
    {
        if (edit is null)
        {
            throw new ToolErrorException(ToolError.InvalidLspData("Missing text edit."));
        }

        return new Change(
            GetOffset(edit.Range.Start, lines),
            GetOffset(edit.Range.End, lines),
            edit.NewText,
            index
        );
    }

    private static int GetOffset(LspPosition position, List<LineSpan> lines)
    {
        var (line, character) = (position.Line, position.Character);
        if (line < 0 || line >= lines.Count || character < 0 || character > lines[line].Length)
        {
            throw new ToolErrorException(
                ToolError.InvalidLspData("The text edit position is outside the document.")
            );
        }

        return lines[line].Start + character;
    }

    internal static List<LineSpan> GetLines(string source)
    {
        var lines = new List<LineSpan>();
        var start = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (!IsLineBreak(source[index]))
            {
                continue;
            }

            lines.Add(new LineSpan(start, index - start));
            if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(new LineSpan(start, source.Length - start));
        return lines;
    }

    // Roslyn's SourceText line breaks, which LSP positions count
    internal static bool IsLineBreak(char character) =>
        character is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';

    private sealed record Change(int Start, int End, string Text, int Index);
}

/// <summary>
/// The offset and length of one line in a text, without its line break.
/// </summary>
internal readonly record struct LineSpan(int Start, int Length);
