// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

/// <summary>
/// The reply of Roslyn's <c>_vs_getProjectContexts</c> extension.
/// </summary>
internal sealed record ProjectContexts(
    [property: JsonPropertyName("_vs_projectContexts")] ProjectContext[]? Contexts = null
);

internal sealed record ProjectContext(
    [property: JsonPropertyName("_vs_id")] string? Id = null,
    [property: JsonPropertyName("_vs_is_miscellaneous")] bool IsMiscellaneous = false
);
