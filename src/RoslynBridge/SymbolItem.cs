// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge;

/// <summary>
/// A one-based line and UTF-16 character.
/// </summary>
internal readonly record struct SourcePosition(int Line, int Character);

/// <summary>
/// A symbol as results show it: a declared or related symbol, or a candidate for an ambiguous name.
/// </summary>
/// <param name="Name">A name that navigate and edit accept, or Roslyn's name outside the workspace.</param>
internal sealed record SymbolItem(string Name, string? Kind)
{
    public string? Detail { get; init; }

    public string? Container { get; init; }

    public string? File { get; init; }

    public SourcePosition? Position { get; init; }

    /// <summary>The end of the whole declaration, known for document symbols.</summary>
    public SourcePosition? End { get; init; }

    public IReadOnlyList<SymbolItem> Children { get; init; } = [];

    /// <summary>The call sites of a caller or callee.</summary>
    public IReadOnlyList<SourcePosition> Calls { get; init; } = [];

    /// <summary>The trimmed source line of each call site or position, when context was requested.</summary>
    public IReadOnlyDictionary<int, string>? Lines { get; init; }
}
