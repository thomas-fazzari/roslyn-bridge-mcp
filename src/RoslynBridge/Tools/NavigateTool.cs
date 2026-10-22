// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

[McpServerToolType]
internal sealed class NavigateTool(LspTool lsp)
{
    internal const string Name = "navigate";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Find the definition, type definition, implementations, references, callers, callees, supertypes or subtypes of a C# symbol, read its hover information or source, list a type's members, or gather the impact of changing it. Name the symbol, or give its one-based position."
    )]
    public Task<CallToolResult> NavigateAsync(
        [Description("Navigation to perform.")] NavigationAction action,
        [Description(ToolDescriptions.Symbol)] string? symbol = null,
        [Description(ToolDescriptions.File)] string? file = null,
        [Description(ToolDescriptions.Line)] int? line = null,
        [Description(ToolDescriptions.Character)] int? character = null,
        [Description(ToolDescriptions.Context)] bool context = false,
        [Description(ToolDescriptions.Limit)] int limit = LspRequest.DefaultResultLimit,
        [Description(ToolDescriptions.Offset)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        lsp.ExecuteAsync(
            new LspRequest
            {
                Limit = limit,
                Offset = offset,
                Action = action.ToLspAction(),
                Symbol = symbol,
                File = file,
                Line = line,
                Character = character,
                Context = context,
            },
            cancellationToken
        );
}

[JsonConverter(typeof(JsonStringEnumConverter<NavigationAction>))]
internal enum NavigationAction
{
    [JsonStringEnumMemberName("definition")]
    Definition = 0,

    [JsonStringEnumMemberName("type_definition")]
    TypeDefinition = 1,

    [JsonStringEnumMemberName("implementation")]
    Implementation = 2,

    [JsonStringEnumMemberName("references")]
    References = 3,

    [JsonStringEnumMemberName("hover")]
    Hover = 4,

    [JsonStringEnumMemberName("callers")]
    Callers = 5,

    [JsonStringEnumMemberName("callees")]
    Callees = 6,

    [JsonStringEnumMemberName("supertypes")]
    Supertypes = 7,

    [JsonStringEnumMemberName("subtypes")]
    Subtypes = 8,

    [JsonStringEnumMemberName("source")]
    Source = 9,

    [JsonStringEnumMemberName("members")]
    Members = 10,

    [JsonStringEnumMemberName("impact")]
    Impact = 11,
}

internal static class NavigationActions
{
    extension(NavigationAction action)
    {
        public LspAction ToLspAction() =>
            action switch
            {
                NavigationAction.Definition => LspAction.Definition,
                NavigationAction.TypeDefinition => LspAction.TypeDefinition,
                NavigationAction.Implementation => LspAction.Implementation,
                NavigationAction.References => LspAction.References,
                NavigationAction.Hover => LspAction.Hover,
                NavigationAction.Callers => LspAction.Callers,
                NavigationAction.Callees => LspAction.Callees,
                NavigationAction.Supertypes => LspAction.Supertypes,
                NavigationAction.Subtypes => LspAction.Subtypes,
                NavigationAction.Source => LspAction.Source,
                NavigationAction.Members => LspAction.Members,
                NavigationAction.Impact => LspAction.Impact,
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
    }
}
