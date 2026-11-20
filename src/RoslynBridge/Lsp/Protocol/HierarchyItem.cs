// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// A call or type hierarchy item. Roslyn expects <paramref name="Raw"/> back unchanged, because it carries its own data.
/// </summary>
internal sealed record HierarchyItem(LspSymbol Symbol, JsonNode Raw);
