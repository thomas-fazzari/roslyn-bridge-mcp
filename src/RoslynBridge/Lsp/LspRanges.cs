// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Serialization;
using RoslynBridge.Results;

namespace RoslynBridge.Lsp;

/// <summary>
/// A zero-based line and UTF-16 character, as LSP sends them.
/// </summary>
internal readonly record struct LspPosition(
    [property: JsonRequired] int Line,
    [property: JsonRequired] int Character
) : IComparable<LspPosition>
{
    public SourcePosition OneBased => new(Line + 1, Character + 1);

    public int CompareTo(LspPosition other) =>
        Line == other.Line ? Character.CompareTo(other.Character) : Line.CompareTo(other.Line);

    public static bool operator <(LspPosition left, LspPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(LspPosition left, LspPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(LspPosition left, LspPosition right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(LspPosition left, LspPosition right) =>
        left.CompareTo(right) >= 0;
}

internal readonly record struct LspRange(
    [property: JsonRequired] LspPosition Start,
    [property: JsonRequired] LspPosition End
)
{
    public bool Contains(LspPosition position) => Start <= position && position <= End;
}

/// <summary>
/// An LSP <c>Location</c>, or a <c>LocationLink</c> whose target fields <see cref="TargetUri"/> and
/// <see cref="TargetSelectionRange"/> stand in for <see cref="Uri"/> and <see cref="Range"/>.
/// </summary>
internal sealed record LspLocation(
    string? Uri = null,
    LspRange? Range = null,
    string? TargetUri = null,
    LspRange? TargetSelectionRange = null
)
{
    public string? ResolvedUri => Uri ?? TargetUri;

    public LspRange? ResolvedRange => Range ?? TargetSelectionRange;
}
