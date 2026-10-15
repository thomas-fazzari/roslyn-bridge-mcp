// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge.Lsp;

internal static class LspParameters
{
    /// <summary>
    /// Returns the parameters of a request about a whole document.
    /// </summary>
    public static JsonObject Document(string uri) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };
}
