// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;

namespace RoslynBridge.Tests.Assertions;

internal static partial class ResultLines
{
    public const int RegexTimeoutMilliseconds = 1_000;

    public static string[] Lines(string text) => text.Split('\n');

    // A preview starts with "proposal <id>: ..." and a code action listing with "proposal <id>"
    public static string ProposalId(string response) =>
        Lines(response)[0].Split(' ')[1].TrimEnd(':');

    // One candidate per line after the error line
    public static string[] CandidateNames(string response) =>
        [.. Lines(response).Skip(1).Select(line => line.Split(' ', 3)[2])];

    // Result lines that are not indented details, totals or notes start with a workspace file
    public static bool IsFileLine(string line) =>
        !line.StartsWith(' ', StringComparison.Ordinal)
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
