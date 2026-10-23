// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Tools;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed partial class ToolSchemaTests
{
    private static readonly Dictionary<string, Tool> _tools = RegisteredTools();
    private static readonly JsonElement[] _schemas =
    [
        .. _tools.Values.SelectMany(tool => Schemas(tool.InputSchema)),
    ];

    // An LSP method name in the lsp tool description
    private static readonly string[] _otherCamelCaseWords = ["executeCommand"];

    [Theory]
    [InlineData(DiagnosticsTool.Name)]
    [InlineData(NavigateTool.Name)]
    [InlineData(BulkNavigateTool.Name)]
    [InlineData(SymbolsTool.Name)]
    public void MarksQueryToolsReadOnly(string name)
    {
        var annotations = _tools[name].Annotations!;

        annotations.ReadOnlyHint.Should().BeTrue();
        annotations.DestructiveHint.Should().BeFalse();
    }

    [Theory]
    [InlineData(EditTool.Name)]
    [InlineData(ServerTool.Name)]
    public void MarksToolsThatCanWriteFilesDestructive(string name)
    {
        var annotations = _tools[name].Annotations!;

        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.DestructiveHint.Should().BeTrue();
    }

    // Claude Code truncates server instructions at 2,048 characters, and Codex may keep only the first 512
    [Fact]
    public void ServerInstructionsFitClientLimits()
    {
        const string instructions = ToolDescriptions.ServerInstructions;
        instructions.Length.Should().BeLessThanOrEqualTo(2_048);
        instructions.Split("\n\n")[0].Length.Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void ExposesExactlyTheSixTools() =>
        _tools
            .Keys.Should()
            .BeEquivalentTo([
                DiagnosticsTool.Name,
                NavigateTool.Name,
                BulkNavigateTool.Name,
                SymbolsTool.Name,
                EditTool.Name,
                ServerTool.Name,
            ]);

    [Theory]
    [InlineData(DiagnosticsTool.Name, new string[0], null)]
    [InlineData(
        NavigateTool.Name,
        new[] { "action" },
        new[]
        {
            "definition",
            "type_definition",
            "implementation",
            "references",
            "hover",
            "callers",
            "callees",
            "supertypes",
            "subtypes",
            "source",
            "members",
            "impact",
            "reads",
            "writes",
        }
    )]
    [InlineData(SymbolsTool.Name, new string[0], null)]
    [InlineData(
        EditTool.Name,
        new[] { "action" },
        new[] { "rename", "rename_file", "code_actions" }
    )]
    [InlineData(
        ServerTool.Name,
        new[] { "action" },
        new[] { "status", "capabilities", "reload", "request" }
    )]
    public void DeclaresFlatArgumentsAndOnlyTheToolActions(
        string name,
        string[] required,
        string[]? actions
    )
    {
        var schema = _tools[name].InputSchema;

        Names(schema, "required").Should().BeEquivalentTo(required);
        if (actions is not null)
        {
            Names(schema.GetProperty("properties").GetProperty("action"), "enum")
                .Should()
                .BeEquivalentTo(actions);
        }
    }

    [Fact]
    public void BulkNavigateTakesQueriesWithTheNavigateActions()
    {
        var schema = _tools[BulkNavigateTool.Name].InputSchema;
        var query = schema.GetProperty("properties").GetProperty("queries").GetProperty("items");

        Names(schema, "required").Should().BeEquivalentTo(["queries"]);
        Names(query, "required").Should().BeEquivalentTo(["action"]);
        Names(query.GetProperty("properties").GetProperty("action"), "enum")
            .Should()
            .BeEquivalentTo(
                Names(
                    _tools[NavigateTool.Name]
                        .InputSchema.GetProperty("properties")
                        .GetProperty("action"),
                    "enum"
                )
            );
    }

    [Fact]
    public void ProseNamesOnlyPublishedParameters()
    {
        var parameters = _schemas.SelectMany(schema =>
            schema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(property => property.Name)
                : []
        );

        ProseWords(CamelCaseWord())
            .Except(parameters, StringComparer.Ordinal)
            .Except(_otherCamelCaseWords, StringComparer.Ordinal)
            .Should()
            .BeEmpty("prose names parameters that no tool publishes");
    }

    [Fact]
    public void ProseNamesOnlyPublishedToolsActionsAndErrorCodes()
    {
        var errorCodes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => (string)field.GetRawConstantValue()!);

        ProseWords(SnakeCaseWord())
            .Except(_tools.Keys, StringComparer.Ordinal)
            .Except(_schemas.SelectMany(schema => Names(schema, "enum")), StringComparer.Ordinal)
            .Except(errorCodes, StringComparer.Ordinal)
            .Should()
            .BeEmpty("prose names tools, actions or error codes that do not exist");
    }

    [GeneratedRegex(
        @"\b[a-z]+(?:[A-Z][a-z0-9]*)+\b",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1_000
    )]
    private static partial Regex CamelCaseWord();

    [GeneratedRegex(@"\b[a-z]+(?:_[a-z]+)+\b", RegexOptions.None, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SnakeCaseWord();

    private static IEnumerable<string> ProseWords(Regex pattern) =>
        _tools
            .Values.Select(tool => tool.Description)
            .Concat(
                _schemas.Select(schema =>
                    schema.TryGetProperty("description", out var description)
                        ? description.GetString()
                        : null
                )
            )
            .Append(ToolDescriptions.ServerInstructions)
            .OfType<string>()
            .SelectMany(text => pattern.Matches(text).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<JsonElement> Schemas(JsonElement schema)
    {
        yield return schema;
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (
                var nested in properties
                    .EnumerateObject()
                    .SelectMany(property => Schemas(property.Value))
            )
            {
                yield return nested;
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var nested in Schemas(items))
            {
                yield return nested;
            }
        }
    }

    private static string[] Names(JsonElement element, string property) =>
        element.TryGetProperty(property, out var values)
            ? [.. values.EnumerateArray().Select(value => value.GetString()!)]
            : [];

    private static Dictionary<string, Tool> RegisteredTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithRoslynTools();
        using var provider = services.BuildServiceProvider();
        return provider
            .GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool)
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }
}
