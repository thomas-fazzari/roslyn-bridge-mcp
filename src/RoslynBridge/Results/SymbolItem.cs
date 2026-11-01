// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <summary>
/// A one-based line and UTF-16 character.
/// </summary>
internal readonly record struct SourcePosition(int Line, int Character);

/// <param name="Name">A name that navigate and edit accept, or Roslyn's name outside the workspace.</param>
internal sealed record SymbolItem(string Name, string? Kind)
{
    public string? Detail { get; init; }

    public string? Container { get; init; }

    public string? File { get; init; }

    public SourcePosition? Position { get; init; }

    /// <summary>
    /// The end of the whole declaration, known for document symbols.
    /// </summary>
    public SourcePosition? End { get; init; }

    public IReadOnlyList<SymbolItem> Children { get; init; } = [];

    public IReadOnlyList<SourcePosition> Calls { get; init; } = [];

    /// <summary>
    /// The base or interface member that the calls go through, for a caller.
    /// </summary>
    public string? Through { get; init; }

    /// <summary>
    /// The trimmed source line of each call site or position, when context was requested.
    /// </summary>
    public IReadOnlyDictionary<int, string>? Lines { get; init; }
}
