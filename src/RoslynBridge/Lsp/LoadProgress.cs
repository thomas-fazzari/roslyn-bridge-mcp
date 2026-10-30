// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// The progress of one operation that Roslyn reports while it loads the workspace.
/// </summary>
/// <param name="Title">What Roslyn is doing, such as Loading Workspace.slnx.</param>
/// <param name="Percentage">The completed share from 0 to 100, or null when Roslyn reports none.</param>
internal sealed record LoadProgress(string Title, int? Percentage)
{
    /// <summary>
    /// The title, followed by the percentage when it is known, such as Loading Workspace.slnx 40%.
    /// </summary>
    public string Text =>
        Percentage is { } percentage
            ? string.Create(CultureInfo.InvariantCulture, $"{Title} {percentage}%")
            : Title;
}
