// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using RoslynBridge.Editing;

namespace RoslynBridge.Queries;

/// <summary>
/// Adds the saved source lines of navigation results, so callers can read each occurrence in place.
/// </summary>
internal static class SourceLines
{
    internal const int MaximumLineCharacters = 200;

    public static Locations Add(Locations locations, WorkspacePaths paths)
    {
        var files = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        return locations with
        {
            Files =
            [
                .. locations.Files.Select(file =>
                    file with
                    {
                        Lines = LinesOf(
                            Read(paths.ResultFilePath(file.File), files),
                            file.Positions
                        ),
                    }
                ),
            ],
        };
    }

    /// <summary>
    /// Items with call sites get the lines of their calls, which are in <paramref name="callSiteFile"/>
    /// when it is set, and in the item's own file otherwise. Other items get the line of their position.
    /// </summary>
    public static SymbolList Add(SymbolList symbols, WorkspacePaths paths, string? callSiteFile)
    {
        var files = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        return symbols with
        {
            Items =
            [
                .. symbols.Items.Select(item =>
                {
                    var ownFile = item.File is null ? null : paths.ResultFilePath(item.File);
                    var (file, positions) =
                        item.Calls.Count > 0
                            ? (callSiteFile ?? ownFile, item.Calls)
                            : (ownFile, item.Position is { } position ? [position] : []);
                    return item with { Lines = LinesOf(Read(file, files), positions) };
                }),
            ],
        };
    }

    private static Dictionary<int, string>? LinesOf(
        string[]? text,
        IEnumerable<SourcePosition> positions
    ) =>
        text is null
            ? null
            : positions
                .Select(position => position.Line)
                .Where(line => line >= 1 && line <= text.Length)
                .Distinct()
                .ToDictionary(line => line, line => Trim(text[line - 1]));

    private static string[]? Read(string? file, Dictionary<string, string[]?> files)
    {
        if (file is null)
        {
            return null;
        }

        if (!files.TryGetValue(file, out var text))
        {
            try
            {
                text = ReadLines(file);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                text = null;
            }

            files[file] = text;
        }

        return text;
    }

    /// <summary>Returns the lines of a saved file, without their line breaks.</summary>
    public static string[] ReadLines(string file)
    {
        var source = File.ReadAllText(file);
        return
        [
            .. TextEdits.GetLines(source).Select(line => source.Substring(line.Start, line.Length)),
        ];
    }

    private static string Trim(string line)
    {
        var text = line.Trim();
        return text.Length <= MaximumLineCharacters
            ? text
            : $"{text.AsSpan(0, MaximumLineCharacters)}…";
    }
}
