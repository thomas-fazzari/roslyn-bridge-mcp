// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Requests;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Integration.Roslyn;

// Reads the text results the bridge returns
public sealed partial class RoslynMcpTests
{
    private const int RegexTimeoutMilliseconds = 1_000;

    private static LspRequest ApplyProposal(LspAction action, string preview) =>
        new()
        {
            Action = action,
            ProposalId = ProposalPattern().Match(preview).Groups["id"].Value,
            Apply = true,
        };

    private static void AssertApplied(string response) => response.Should().StartWith("applied:");

    private static void AssertError(CallToolResult response, string expectedCode)
    {
        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(expectedCode);
    }

    private static void AssertSingleLocation(string response, string expectedFile) =>
        Locations(response).Should().ContainSingle().Which.Should().Be(expectedFile, response);

    /// <summary>
    /// Starts Roslyn and waits for the project load, so a file written next reaches Roslyn
    /// as a change to the loaded workspace.
    /// </summary>
    private static async Task StartRoslynAsync(RoslynTestWorkspace workspace) =>
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = ConsumerFile }
        );

    private static async Task<Diagnostic[]> DiagnosticsAsync(RoslynTestWorkspace workspace) =>
        Diagnostics(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = ProblemsFile }
            )
        );

    private static string[] Lines(string text) => text.Split('\n');

    private static int ActionIndex(string listing, Func<string, bool> match)
    {
        var action = Lines(listing).Should().ContainSingle(line => match(line), listing).Which;
        return int.Parse(
            action[..action.IndexOf(' ', StringComparison.Ordinal)],
            CultureInfo.InvariantCulture
        );
    }

    // The lines of one impact section, up to the next section header
    private static IEnumerable<string> Section(string[] lines, string name) =>
        lines
            .SkipWhile(line => !string.Equals(line, "# " + name, StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(line => !line.StartsWith("# ", StringComparison.Ordinal));

    // Result lines that are not indented details, totals or notes start with a workspace file
    private static bool IsFileLine(string line) =>
        !line.StartsWith(' ')
        && !line.StartsWith("truncated:", StringComparison.Ordinal)
        && !line.StartsWith("incomplete:", StringComparison.Ordinal)
        && !SummaryPattern().IsMatch(line);

    private static string FileOf(string line)
    {
        var token = line.TrimStart().Split(' ')[0];
        var position = PositionSuffixPattern().Match(token);
        return position.Success ? token[..position.Index] : token;
    }

    // A symbol line ends with the name after its location and kind
    private static string SymbolName(string line)
    {
        var parts = line.TrimStart().Split(' ', 3);
        var name = parts[2];
        var container = name.IndexOf(" in ", StringComparison.Ordinal);
        return container < 0 ? name : name[..container];
    }

    private static string[] Locations(string response) =>
        [.. Lines(response).Where(IsFileLine).Select(FileOf)];

    private static string[] Candidates(CallToolResult response) =>
        [.. Lines(ResponseText(response)).Skip(1)];

    private static string[] PreviewFiles(string preview) =>
        [
            .. Lines(preview)
                .Select(line => OperationPattern().Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["path"].Value),
        ];

    private static List<(string Before, string After)> Hunks(string preview)
    {
        var hunks = new List<(string Before, string After)>();
        foreach (var line in Lines(preview))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                hunks.Add((string.Empty, string.Empty));
            }
            else if (hunks.Count > 0 && line.StartsWith("- ", StringComparison.Ordinal))
            {
                hunks[^1] = (hunks[^1].Before + line[2..] + "\n", hunks[^1].After);
            }
            else if (hunks.Count > 0 && line.StartsWith("+ ", StringComparison.Ordinal))
            {
                hunks[^1] = (hunks[^1].Before, hunks[^1].After + line[2..] + "\n");
            }
        }

        return hunks;
    }

    private static Diagnostic[] Diagnostics(string response) =>
        [
            .. Lines(response)
                .Select(line => DiagnosticPattern().Match(line))
                .Where(match => match.Success)
                .Select(match => new Diagnostic(
                    match.Groups["severity"].Value,
                    match.Groups["code"].Value
                )),
        ];

    private static DiagnosticSummary Summary(string response)
    {
        var match = Lines(response)
            .Select(line => SummaryPattern().Match(line))
            .Single(m => m.Success);
        return new DiagnosticSummary(
            Number(match.Groups["checked"]),
            Number(match.Groups["files"]),
            Number(match.Groups["total"]),
            Number(match.Groups["below"])
        );

        static int Number(Group group) =>
            group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(
        @"^proposal (?<id>\w+)",
        RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex ProposalPattern();

    [GeneratedRegex(
        @"^(?:change|create|delete) (?<path>.+)$|^rename .+ -> (?<path>.+)$",
        RegexOptions.None,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex OperationPattern();

    [GeneratedRegex(
        @"^  \d+:\d+ (?<severity>\w+) (?<code>\S+) ",
        RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(
        @"^(?<checked>\d+) files? checked, (?:no diagnostics|(?<total>\d+) diagnostics? in (?<files>\d+) files?)(?:, (?<below>\d+) below the severity)?$",
        RegexOptions.None,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@":\d+:\d+$", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex PositionSuffixPattern();

    private readonly record struct Diagnostic(string Severity, string Code);

    private readonly record struct DiagnosticSummary(
        int FilesChecked,
        int FilesWithDiagnostics,
        int Total,
        int BelowSeverity
    );
}
