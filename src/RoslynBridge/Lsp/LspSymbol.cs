// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// A document symbol, workspace symbol or hierarchy item.
/// </summary>
/// <param name="Glyph">The Roslyn extension that encodes the kind and accessibility of a document symbol.</param>
internal sealed record LspSymbol(
    string Name,
    int Kind,
    string? Detail = null,
    string? ContainerName = null,
    string? Uri = null,
    LspRange? Range = null,
    LspRange? SelectionRange = null,
    LspLocation? Location = null,
    LspSymbol[]? Children = null,
    int? Glyph = null
);

/// <summary>
/// An incoming call has <see cref="From"/>, and an outgoing call has <see cref="To"/>.
/// </summary>
internal sealed record LspCall(
    LspSymbol? From = null,
    LspSymbol? To = null,
    LspRange[]? FromRanges = null
);

internal sealed record LspHighlight(LspRange Range, int? Kind = null);
