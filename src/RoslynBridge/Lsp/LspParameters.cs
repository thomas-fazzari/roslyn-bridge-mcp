// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

internal static class LspParameters
{
    public static JsonObject Document(string uri) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };
}
