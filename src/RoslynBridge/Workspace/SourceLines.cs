// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;

namespace RoslynBridge.Workspace;

/// <summary>
/// Reads the trimmed source lines of positions, and each file once.
/// </summary>
internal sealed class SourceLines
{
    internal const int MaximumLineCharacters = 200;

    private readonly Dictionary<string, string[]?> _files = [with(StringComparer.Ordinal)];

    /// <summary>
    /// Returns null when the file is null or cannot be read.
    /// </summary>
    public Dictionary<int, string>? At(string? file, IEnumerable<SourcePosition> positions) =>
        Of(file) is not { } text
            ? null
            : positions
                .Select(position => position.Line)
                .Where(line => line >= 1 && line <= text.Length)
                .Distinct()
                .ToDictionary(line => line, line => Trim(text[line - 1]));

    /// <summary>
    /// Returns null when the file cannot be read.
    /// </summary>
    public static string[]? TryReadLines(string? file) =>
        file is not null && ReadLines(file) is string[] lines ? lines : null;

    public static Result<string[]> ReadLines(string file)
    {
        string source;
        try
        {
            source = File.ReadAllText(file);
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }

        return TextEdits
            .GetLines(source)
            .Select(line => source.Substring(line.Start, line.Length))
            .ToArray();
    }

    /// <summary>
    /// The untrimmed lines of the file, or null when the file is null or cannot be read.
    /// </summary>
    public string[]? Of(string? file)
    {
        if (file is null)
            return null;

        if (!_files.TryGetValue(file, out var text))
        {
            text = TryReadLines(file);
            _files[file] = text;
        }

        return text;
    }

    private static string Trim(string line)
    {
        var text = line.Trim();
        return text.Length <= MaximumLineCharacters
            ? text
            : $"{text.AsSpan(0, MaximumLineCharacters)}…";
    }
}
