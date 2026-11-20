// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// A <c>workspace/executeCommand</c> request parameter, sent as it is.
/// </summary>
internal sealed record LspCommand(JsonObject Raw)
{
    public JsonNode ToJson() => Raw.DeepClone();
}
