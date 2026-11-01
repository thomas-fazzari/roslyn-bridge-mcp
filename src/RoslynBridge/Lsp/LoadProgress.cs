// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <param name="Percentage">From 0 to 100, or null when Roslyn reports none.</param>
internal sealed record LoadProgress(string Title, int? Percentage)
{
    public string Text =>
        Percentage is { } percentage
            ? string.Create(CultureInfo.InvariantCulture, $"{Title} {percentage}%")
            : Title;
}
