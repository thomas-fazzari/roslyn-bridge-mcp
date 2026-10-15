// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StreamJsonRpc.Protocol;

namespace RoslynBridge.Lsp;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(CommonErrorData))]
internal partial class LspJsonContext : JsonSerializerContext;
