// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Extension;

/// <param name="Line">Zero-based, as in LSP.</param>
/// <param name="Character">Zero-based UTF-16 offset, as in LSP.</param>
internal sealed record ReferencesRequest(
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
