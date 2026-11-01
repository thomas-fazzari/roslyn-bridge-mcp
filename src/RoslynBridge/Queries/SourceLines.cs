// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

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
    /// Call sites are in <paramref name="callSiteFile"/> when it is set, otherwise in the item's own file.
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

    /// <summary>
    /// Returns null when the file cannot be read.
    /// </summary>
    public static string[]? TryReadLines(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            return ReadLines(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string[] ReadLines(string file)
    {
        var source = File.ReadAllText(file);
        return
        [
            .. TextEdits.GetLines(source).Select(line => source.Substring(line.Start, line.Length)),
        ];
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
            text = TryReadLines(file);
            files[file] = text;
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
