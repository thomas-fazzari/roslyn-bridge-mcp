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
        public Result<T> Read<T>() => Deserialize(() => node.Deserialize<T>(Options));

        /// <summary>
        /// Returns none for a null or non-array result.
        /// </summary>
        public Result<T[]> ReadArray<T>() =>
            node is JsonArray ? node.Read<T[]>() : Array.Empty<T>();
    }

    /// <summary>
    /// Reads JSON text that Roslyn returned inside a string, such as an extension response.
    /// </summary>
    public static Result<T> Parse<T>(string json) =>
        Deserialize(() => JsonSerializer.Deserialize<T>(json, Options));

    private static Result<T> Deserialize<T>(Func<T?> read)
    {
        T? value;
        try
        {
            value = read();
        }
        catch (JsonException exception)
        {
            return InvalidData(exception.Message);
        }

        return value is null ? InvalidData("The value is null.") : value;
    }

    private static ToolError InvalidData(string reason) =>
        ToolError.InvalidLspData($"Roslyn returned invalid data: {reason}");
}
