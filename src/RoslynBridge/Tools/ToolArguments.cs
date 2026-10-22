// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using Fastenshtein;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynBridge.Tools;

/// <summary>
/// Checks the arguments of a tool call against the input schema the tool publishes, which the SDK does not enforce.
/// </summary>
internal static class ToolArguments
{
    private const int MaximumTypoDistance = 2;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Validate(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    ) =>
        async (context, cancellationToken) =>
        {
            if (
                context.MatchedPrimitive is McpServerTool tool
                && CheckArguments(
                    tool.ProtocolTool,
                    context.Params?.Arguments
                        ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                )
                    is { } error
            )
            {
                return LspTool.Failure(error);
            }

            try
            {
                return await next(context, cancellationToken);
            }
            // The SDK hides the message of a binding failure, such as an unknown key inside a nested object
            catch (JsonException exception)
            {
                return LspTool.Failure(ToolError.InvalidRequest(exception.Message));
            }
        };

    private static ToolError? CheckArguments(Tool tool, IDictionary<string, JsonElement> arguments)
    {
        var known = tool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value,
                    StringComparer.Ordinal
                )
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments.ToArray())
        {
            if (!known.TryGetValue(name, out var property))
            {
                return UnknownArgument(tool.Name, name, known.Keys);
            }

            if (value.ValueKind is not JsonValueKind.String || !DeclaresArray(property))
            {
                continue;
            }

            if (ParseArray(value.GetString()!) is not { } array)
            {
                return ToolError.InvalidRequest($"Argument '{name}' must be a JSON array.");
            }

            arguments[name] = array;
        }

        return MissingArgument(tool, arguments);
    }

    private static ToolError? MissingArgument(Tool tool, IDictionary<string, JsonElement> arguments)
    {
        if (!tool.InputSchema.TryGetProperty("required", out var required))
        {
            return null;
        }

        var missing = required
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .FirstOrDefault(name => !arguments.ContainsKey(name));
        return missing is null
            ? null
            : ToolError.InvalidRequest($"Missing argument '{missing}' for {tool.Name}.");
    }

    private static ToolError UnknownArgument(
        string tool,
        string name,
        IReadOnlyCollection<string> valid
    )
    {
        var message = $"Unknown argument '{name}' for {tool}.";
        if (Suggest(name, valid) is { } suggestion)
        {
            message += $" Did you mean '{suggestion}'?";
        }

        return ToolError.InvalidRequest($"{message} Valid arguments: {string.Join(", ", valid)}.");
    }

    private static bool DeclaresArray(JsonElement property) =>
        property.TryGetProperty("type", out var type)
        && (
            type.ValueKind is JsonValueKind.Array
                ? type.EnumerateArray().Any(item => item.ValueEquals("array"))
                : type.ValueEquals("array")
        );

    private static JsonElement? ParseArray(string text)
    {
        try
        {
            var array = JsonElement.Parse(text);
            return array.ValueKind is JsonValueKind.Array ? array : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? suggestion = null;
        var best = int.MaxValue;
        var tied = false;
        var lowerName = name.ToLowerInvariant();
        foreach (var candidate in candidates)
        {
            var distance = Levenshtein.Distance(lowerName, candidate.ToLowerInvariant());
            if (distance > MaximumTypoDistance)
            {
                continue;
            }

            if (distance < best)
            {
                (suggestion, best, tied) = (candidate, distance, false);
            }
            else if (distance == best)
            {
                tied = true;
            }
        }

        return tied ? null : suggestion;
    }
}
