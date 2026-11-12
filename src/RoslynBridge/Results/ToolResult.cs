// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

internal closed record ToolResult;

internal sealed record JsonResult(JsonNode? Value) : ToolResult;
