// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;

namespace RoslynBridge.Results;

/// <summary>
/// What a tool call returns before rendering. Each kind of result derives from it.
/// </summary>
internal abstract record ToolResult;

/// <summary>
/// A result kept as JSON, such as a raw LSP response.
/// </summary>
internal sealed record JsonResult(JsonNode? Value) : ToolResult;
