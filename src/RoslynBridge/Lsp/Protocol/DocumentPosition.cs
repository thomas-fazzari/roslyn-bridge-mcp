// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// The textDocument and zero-based position of a request.
/// </summary>
internal sealed record DocumentPosition(string Uri, LspPosition Position);

/// <summary>
/// The textDocument and zero-based range of a request.
/// </summary>
internal sealed record DocumentRange(string Uri, LspRange Range);
