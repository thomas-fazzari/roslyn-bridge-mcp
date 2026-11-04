// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// The unused symbols that the Roslyn extension reports.
/// </summary>
/// <param name="Symbols">In file and position order.</param>
/// <param name="Skipped">Declarations left out because a framework may reach them by reflection.</param>
/// <param name="Unmatched">Requested files that no project compiles.</param>
internal sealed record UnusedReport(
    [property: JsonRequired] UnusedEntry[] Symbols,
    [property: JsonRequired] int Skipped,
    [property: JsonRequired] string[] Unmatched
);

/// <param name="Via">Indexes in <see cref="UnusedReport.Symbols"/> of the unused code that uses it.</param>
internal sealed record UnusedEntry(
    [property: JsonRequired] LspSymbol Symbol,
    [property: JsonRequired] int[] Via,
    [property: JsonRequired] bool TestsOnly
);
