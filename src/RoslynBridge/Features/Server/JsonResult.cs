// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Features.Server;

internal sealed record JsonResult(JsonNode? Value) : ToolResult
{
    internal override void Write(StringBuilder text, int budget) =>
        text.Append(ResultText.Json(Value));
}
