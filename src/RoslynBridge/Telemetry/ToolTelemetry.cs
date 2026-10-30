// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Output;

namespace RoslynBridge.Telemetry;

internal static class ToolTelemetry
{
    internal const string ToolName = "gen_ai.tool.name";
    internal const string Action = "roslyn_bridge.action";

    // OpenTelemetry's placeholder for a value outside a bounded set
    private const string OtherTool = "_OTHER";
    private const string ResultCharacters = "roslyn_bridge.result.characters";
    private const string OperationName = "gen_ai.operation.name";
    private const string ExecuteTool = "execute_tool";
    private const string QueryCount = "roslyn_bridge.bulk.queries";
    private const string ActionArgument = "action";
    private const string QueriesArgument = "queries";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Measure(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    ) =>
        async (context, cancellationToken) =>
        {
            var tool = (context.MatchedPrimitive as McpServerTool)?.ProtocolTool;
            var name = tool?.Name ?? OtherTool;
            using var activity = BridgeTelemetry.Source.StartActivity($"{ExecuteTool} {name}");
            activity?.SetTag(OperationName, ExecuteTool);
            var tags = new TagList { { ToolName, name } };
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await next(context, cancellationToken);
                // The argument check rewrites a JSON string into an array, so the arguments are read after it ran
                var arguments = context.Params?.Arguments;
                if (tool is not null && ActionOf(tool, arguments) is { } action)
                {
                    tags.Add(Action, action);
                }

                if (QueriesOf(arguments) is { } queries)
                {
                    activity?.SetTag(QueryCount, queries);
                }

                if (result.IsError is true && ErrorCodeOf(result) is { } code)
                {
                    tags.Add(BridgeTelemetry.ErrorType, code);
                }

                var characters = result
                    .Content.OfType<TextContentBlock>()
                    .Sum(static block => block.Text.Length);
                activity?.SetTag(ResultCharacters, characters);
                BridgeTelemetry.ResultSize.Record(characters, tags);
                return result;
            }
            catch (Exception exception)
            {
                tags.Add(BridgeTelemetry.ErrorType, BridgeTelemetry.ErrorTypeOf(exception));
                throw;
            }
            finally
            {
                BridgeTelemetry.Complete(activity, BridgeTelemetry.ToolDuration, started, tags);
            }
        };

    private static string? ActionOf(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (
            arguments?.TryGetValue(ActionArgument, out var action) != true
            || action.ValueKind is not JsonValueKind.String
            || !tool.InputSchema.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(ActionArgument, out var property)
            || !property.TryGetProperty("enum", out var values)
        )
        {
            return null;
        }

        return values.EnumerateArray().Any(value => value.ValueEquals(action.GetString()))
            ? action.GetString()
            : null;
    }

    private static int? QueriesOf(IDictionary<string, JsonElement>? arguments) =>
        arguments?.TryGetValue(QueriesArgument, out var queries) == true
        && queries.ValueKind is JsonValueKind.Array
            ? queries.GetArrayLength()
            : null;

    private static string? ErrorCodeOf(CallToolResult result) =>
        result.Content is [TextContentBlock { Text: var text }, ..]
            ? ResultText.ErrorCode(text)
            : null;
}
