// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

/// <summary>
/// A one-based line and UTF-16 character.
/// </summary>
internal readonly record struct SourcePosition(int Line, int Character);

/// <summary>
/// A position in a workspace file or in an external or generated document.
/// </summary>
internal readonly record struct SourceLocation(LocationFile File, SourcePosition Position)
{
    /// <summary>
    /// Orders by the file text with an ordinal comparison, then by line and character.
    /// </summary>
    public static IComparer<SourceLocation> Order { get; } =
        Comparer<SourceLocation>.Create(Compare);

    private static int Compare(SourceLocation first, SourceLocation second)
    {
        var comparison = string.CompareOrdinal(first.File.Text, second.File.Text);
        if (comparison != 0)
            return comparison;

        comparison = first.Position.Line.CompareTo(second.Position.Line);
        return comparison != 0
            ? comparison
            : first.Position.Character.CompareTo(second.Position.Character);
    }
}
