// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;

namespace RoslynBridge.Tests.Assertions;

// Reads the lines of the text results the bridge returns
internal static partial class ResultLines
{
    public const int RegexTimeoutMilliseconds = 1_000;

    public static string[] Lines(string text) => text.Split('\n');

    // Result lines that are not indented details, totals or notes start with a workspace file
    public static bool IsFileLine(string line) =>
        !line.StartsWith(' ')
        && !line.StartsWith("truncated:", StringComparison.Ordinal)
        && !line.StartsWith("incomplete:", StringComparison.Ordinal)
        && !SummaryPattern().IsMatch(line);

    public static string FileOf(string line)
    {
        var token = line.TrimStart().Split(' ')[0];
        var position = PositionSuffixPattern().Match(token);
        return position.Success ? token[..position.Index] : token;
    }

    public static string[] Locations(string response) =>
        [.. Lines(response).Where(IsFileLine).Select(FileOf)];

    [GeneratedRegex(
        @"^(?<checked>\d+) files? checked, (?:no diagnostics|(?<total>\d+) diagnostics? in (?<files>\d+) files?)(?:, (?<below>\d+) below the severity)?$",
        RegexOptions.None,
        RegexTimeoutMilliseconds
    )]
    public static partial Regex SummaryPattern();

    [GeneratedRegex(@":\d+:\d+$", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex PositionSuffixPattern();
}
