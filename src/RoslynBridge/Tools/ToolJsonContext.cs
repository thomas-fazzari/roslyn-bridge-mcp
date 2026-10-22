// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

/// <summary>
/// JSON metadata for tool arguments, which Native AOT cannot build by reflection.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NavigationAction))]
[JsonSerializable(typeof(BulkQuery))]
[JsonSerializable(typeof(BulkQuery[]))]
[JsonSerializable(typeof(EditAction))]
[JsonSerializable(typeof(ServerAction))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(JsonObject))]
internal partial class ToolJsonContext : JsonSerializerContext;
