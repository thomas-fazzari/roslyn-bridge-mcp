// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

/// <summary>
/// Reuses the formatter's RequestId converter, which is internal and cannot be referenced by JSON source generation.
/// </summary>
internal sealed class RequestIdResolver(JsonConverter<RequestId> converter) : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
        type == typeof(RequestId)
            ? JsonMetadataServices.CreateValueInfo<RequestId>(options, converter)
            : null;
}
