// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Queries;

/// <summary>
/// The occurrences found in one file, a workspace-relative path or the URI of an external or generated file.
/// </summary>
/// <param name="Lines">The trimmed source line of each occurrence line, when context was requested.</param>
internal sealed record FileLocations(
    string File,
    IReadOnlyList<SourcePosition> Positions,
    IReadOnlyDictionary<int, string>? Lines = null
);

/// <param name="Total">Occurrences before limiting.</param>
/// <param name="Generated">Source-generated occurrences left out.</param>
internal sealed record Locations(
    IReadOnlyList<FileLocations> Files,
    int Total,
    bool Truncated,
    int Generated
) : ToolResult;

/// <param name="Total">Symbols before limiting, nested ones included.</param>
/// <param name="Accesses">Property, field and constant accesses left out of callees.</param>
internal sealed record SymbolList(
    IReadOnlyList<SymbolItem> Items,
    int Total,
    bool Truncated,
    int Accesses = 0
) : ToolResult;

/// <summary>
/// Hover information as plain text.
/// </summary>
internal sealed record HoverText(string Text) : ToolResult;
