// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RoslynBridge.Requests;
using RoslynBridge.Tools;
using StreamJsonRpc.Protocol;

namespace RoslynBridge;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NavigationAction))]
[JsonSerializable(typeof(EditAction))]
[JsonSerializable(typeof(ServerAction))]
[JsonSerializable(typeof(DiagnosticSeverity))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(CommonErrorData))]
internal partial class BridgeJsonContext : JsonSerializerContext;
