// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;

namespace RoslynBridge;

/// <summary>
/// What a tool call returns before rendering. Each kind of result derives from it.
/// </summary>
internal abstract record ToolResult;

/// <summary>
/// A result kept as JSON, such as a raw LSP response.
/// </summary>
internal sealed record JsonResult(JsonNode? Value) : ToolResult;
