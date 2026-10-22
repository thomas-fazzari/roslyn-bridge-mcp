// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
