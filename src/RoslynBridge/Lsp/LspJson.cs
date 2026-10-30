// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RoslynBridge.Lsp;

internal static class LspJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    extension(JsonNode? node)
    {
        /// <summary>
        /// Reads a value that the protocol requires, and reports a missing or malformed field as invalid LSP data.
        /// </summary>
        public T Read<T>()
        {
            try
            {
                return node.Deserialize<T>(Options)
                    ?? throw new JsonException("The value is null.");
            }
            catch (JsonException exception)
            {
                throw new ToolErrorException(
                    ToolError.InvalidLspData($"Roslyn returned invalid data: {exception.Message}")
                );
            }
        }

        /// <returns>The elements of an array result, or none for a null result or any other kind of value.</returns>
        public T[] ReadArray<T>() => node is JsonArray ? node.Read<T[]>() : [];
    }
}
