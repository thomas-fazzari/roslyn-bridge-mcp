// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Extension;

/// <param name="Line">Zero-based, as in LSP.</param>
/// <param name="Character">Zero-based UTF-16 offset, as in LSP.</param>
internal sealed record PositionRequest(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character
);

/// <param name="Found">False when the caller must ask textDocument/references instead.</param>
/// <param name="Locations">Workspace references, in the shape of LSP locations.</param>
/// <param name="Written">The locations that documentHighlight marks as writes.</param>
/// <param name="Generated">References in source-generated documents, which are only counted.</param>
internal sealed record ReferencesResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("locations")] IReadOnlyList<LspLocation> Locations,
    [property: JsonPropertyName("written")] IReadOnlyList<LspLocation> Written,
    [property: JsonPropertyName("generated")] int Generated
);

/// <param name="Found">False when the caller must ask callHierarchy/incomingCalls instead.</param>
/// <param name="Calls">Workspace callers, in the shape of LSP incoming calls.</param>
internal sealed record CallersResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("calls")] IReadOnlyList<IncomingCall> Calls
);

/// <param name="Through">The base or interface member the calls go through, null for direct calls.</param>
internal sealed record IncomingCall(
    [property: JsonPropertyName("from")] CallItem From,
    [property: JsonPropertyName("fromRanges")] IReadOnlyList<LspRange> FromRanges,
    [property: JsonPropertyName("through")] string? Through
);

/// <param name="Found">False when the caller must ask callHierarchy/outgoingCalls instead.</param>
/// <param name="Calls">Callees declared in source, in the shape of LSP outgoing calls. Properties, fields and events are listed too.</param>
internal sealed record CalleesResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("calls")] IReadOnlyList<OutgoingCall> Calls
);

/// <param name="FromRanges">The call sites, in the requested declaration.</param>
internal sealed record OutgoingCall(
    [property: JsonPropertyName("to")] CallItem To,
    [property: JsonPropertyName("fromRanges")] IReadOnlyList<LspRange> FromRanges
);

/// <param name="Files">Full paths of the C# files whose declarations to check.</param>
internal sealed record UnusedRequest(
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files
);

/// <param name="Symbols">In file and position order.</param>
/// <param name="Skipped">Declarations left out because a framework may reach them by reflection.</param>
/// <param name="Unmatched">Requested files that no project compiles.</param>
internal sealed record UnusedResponse(
    [property: JsonPropertyName("symbols")] IReadOnlyList<UnusedSymbol> Symbols,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<string> Unmatched
);

/// <param name="Via">Indexes of the unused symbols that reference this one, in ascending order.</param>
/// <param name="TestsOnly">True when test projects reference it.</param>
internal sealed record UnusedSymbol(
    [property: JsonPropertyName("symbol")] CallItem Symbol,
    [property: JsonPropertyName("via")] IReadOnlyList<int> Via,
    [property: JsonPropertyName("testsOnly")] bool TestsOnly
);

/// <param name="Kind">An LSP SymbolKind.</param>
internal sealed record CallItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("selectionRange")] LspRange SelectionRange
);

/// <param name="Kind">An LSP SymbolKind.</param>
/// <param name="Uri">Null, like the range, for a symbol without a declaration in a file, such as a metadata symbol.</param>
/// <param name="SelectionRange">The name in its declaration.</param>
internal sealed record ExpressionSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("selectionRange")] LspRange? SelectionRange
);

/// <param name="Found">False when the position has no expression and declares no name.</param>
/// <param name="Text">The expression with its white space collapsed, or the declared name.</param>
/// <param name="Kind">The syntax kind of the expression, or of the declaration.</param>
/// <param name="Declaration">True when the position is on the name that a declaration introduces.</param>
/// <param name="Type">The type of the expression, or the declared type. The return type for a method.</param>
/// <param name="Nullability">The annotation and the flow state, such as NotAnnotated/NotNull.</param>
/// <param name="Conversion">Null for an identity conversion.</param>
/// <param name="Constant">Null when the expression is not a constant.</param>
/// <param name="Symbol">The original definition of the bound symbol.</param>
/// <param name="Instantiation">The constructed generic or reduced extension method that the code uses.</param>
/// <param name="UnboundReason">The CandidateReason of an expression that binds to no symbol.</param>
internal sealed record ExpressionResponse(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("declaration")] bool Declaration,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("convertedType")] string? ConvertedType = null,
    [property: JsonPropertyName("nullability")] string? Nullability = null,
    [property: JsonPropertyName("conversion")] string? Conversion = null,
    [property: JsonPropertyName("constant")] string? Constant = null,
    [property: JsonPropertyName("symbol")] ExpressionSymbol? Symbol = null,
    [property: JsonPropertyName("instantiation")] string? Instantiation = null,
    [property: JsonPropertyName("unboundReason")] string? UnboundReason = null,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ExpressionSymbol>? Candidates = null,
    [property: JsonPropertyName("documentation")] string? Documentation = null
);

internal sealed record LspLocation(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] LspRange Range
);

internal sealed record LspRange(
    [property: JsonPropertyName("start")] LspPosition Start,
    [property: JsonPropertyName("end")] LspPosition End
);

internal sealed record LspPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character
);
