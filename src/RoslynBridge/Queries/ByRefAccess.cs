// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Queries;

/// <summary>
/// Tells how a by-reference argument uses its variable. Roslyn highlights every such argument as a write.
/// </summary>
internal static class ByRefAccess
{
    private static readonly string[] _readers = ["Volatile.Read", "Interlocked.Read"];
    private static readonly string[] _writers = ["Volatile.Write"];

    /// <summary>
    /// Reads the text before the highlight. A <c>ref</c> argument both reads and writes, unless the
    /// call is a known reader or writer, and an <c>out</c> argument only writes.
    /// </summary>
    /// <returns>The use of the argument, or <see cref="ByRefUse.None"/> when it is not passed by reference.</returns>
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
        if (EndsWithKeyword(before, "readonly"))
        {
            before = before[..^"readonly".Length].TrimEnd();
        }

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

    /// <returns>The text without a trailing member path such as <c>this.</c> or <c>owner.field.</c>.</returns>
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

    /// <returns>Whether the text ends with the keyword as a whole word.</returns>
    private static bool EndsWithKeyword(string text, string keyword) =>
        text.EndsWith(keyword, StringComparison.Ordinal)
        && (text.Length == keyword.Length || !IsNameCharacter(text[^(keyword.Length + 1)]));

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.';
}

/// <summary>How an argument passed by reference uses its variable.</summary>
internal enum ByRefUse
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = 3,
}
