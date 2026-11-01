// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal static class ByRefAccess
{
    private static readonly string[] _readers = ["Volatile.Read", "Interlocked.Read"];
    private static readonly string[] _writers = ["Volatile.Write"];

    /// <summary>
    /// Roslyn highlights every by-reference argument as a write. A <c>ref</c> argument reads and writes unless the call is a known reader or writer.
    /// </summary>
    public static ByRefUse Of(string[]? lines, SourcePosition position)
    {
        if (
            lines is null
            || position.Line < 1
            || position.Line > lines.Length
            || position.Character < 1
        )
        {
            return ByRefUse.None;
        }

        var line = lines[position.Line - 1];
        var before = WithoutQualifier(line[..Math.Min(position.Character - 1, line.Length)])
            .TrimEnd();
        if (EndsWithKeyword(before, "out"))
        {
            return ByRefUse.Write;
        }

        if (!EndsWithKeyword(before, "ref"))
        {
            return ByRefUse.None;
        }

        var call = before[..^"ref".Length].TrimEnd();
        if (!call.EndsWith('('))
        {
            return ByRefUse.ReadWrite;
        }

        call = call[..^1].TrimEnd();
        if (_readers.Any(name => call.EndsWith(name, StringComparison.Ordinal)))
        {
            return ByRefUse.Read;
        }

        return _writers.Any(name => call.EndsWith(name, StringComparison.Ordinal))
            ? ByRefUse.Write
            : ByRefUse.ReadWrite;
    }

    private static string WithoutQualifier(string text)
    {
        if (!text.EndsWith('.'))
        {
            return text;
        }

        var end = text.Length - 1;
        while (end > 0 && IsNameCharacter(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    private static bool EndsWithKeyword(string text, string keyword) =>
        text.EndsWith(keyword, StringComparison.Ordinal)
        && (text.Length == keyword.Length || !IsNameCharacter(text[^(keyword.Length + 1)]));

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.';
}

internal enum ByRefUse
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = 3,
}
