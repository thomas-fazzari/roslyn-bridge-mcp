// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;
using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Features.Symbols;

/// <param name="Via">The names of the unused symbols that reference it.</param>
/// <param name="TestsOnly">True when test projects reference it.</param>
internal sealed record UnusedSymbol(SymbolItem Symbol, IReadOnlyList<string> Via, bool TestsOnly);

/// <param name="Skipped">Declarations left out because a framework may reach them by reflection.</param>
/// <param name="Incomplete">Selected files that no project compiles, so they were not checked.</param>
internal sealed record UnusedSymbols(
    IReadOnlyList<UnusedSymbol> Items,
    int Total,
    int? NextOffset,
    int FilesChecked,
    int Skipped,
    IReadOnlyList<string> Incomplete
) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var symbol in Items)
        {
            AppendSymbol(text, symbol.Symbol, indent: string.Empty);
            foreach (var user in symbol.Via)
            {
                text.Append(Indent).Append("via ").Append(user).Append('\n');
            }

            if (symbol.TestsOnly)
                text.Append(Indent).Append("tests only\n");
        }

        text.Append(Count(FilesChecked, "file", "files"))
            .Append(" checked, ")
            .Append(
                Total == 0 ? "no unused symbols" : Count(Total, "unused symbol", "unused symbols")
            );
        if (Skipped > 0)
            text.Append(CultureInfo.InvariantCulture, $", {Skipped} skipped as framework code");

        text.Append('\n');
        if (Total > 0)
            AppendPageEnd(text, Total, Items.Count == 0, NextOffset, "unused symbols");

        if (Incomplete.Count > 0)
        {
            text.Append("incomplete: not in a project, not checked: ")
                .AppendJoin(' ', Incomplete)
                .Append('\n');
        }

        if (Total > 0)
            text.Append("reflection or code outside the workspace may still use a public symbol\n");
    }
}
