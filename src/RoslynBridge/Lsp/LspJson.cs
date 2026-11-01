// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
        /// Reports a missing or malformed field as invalid LSP data.
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

        /// <summary>
        /// Returns none for a null or non-array result.
        /// </summary>
        public T[] ReadArray<T>() => node is JsonArray ? node.Read<T[]>() : [];
    }
}
