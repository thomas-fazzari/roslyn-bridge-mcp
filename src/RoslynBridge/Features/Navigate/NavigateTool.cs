// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Navigate;

internal sealed class NavigateTool(
    ToolPipeline pipeline,
    IToolHandler<NavigateRequest, ToolResult> handler
) : IRoslynTool
{
    private const string ActionDescription = "Navigation to perform.";
    private const string ContextDescription =
        "Show the trimmed source line of each result position. Uses more tokens.";
    private const string InheritedDescription =
        "For members, also list the members that base classes in the workspace pass on, after the type's own.";

    public static string Name => "navigate";

    public static string Description =>
        "Find the definition, type definition, implementations, references, callers, callees, supertypes or subtypes of a C# symbol, read its hover information or source, list a type's members, gather the impact of changing it, or list the reads or writes of a field, property, local, parameter or event. Name the symbol, or give its one-based position.";

    public static bool ReadOnly => true;

    public Task<CallToolResult> NavigateAsync(
        [Description(ActionDescription)] NavigationAction action,
        [Description(SymbolTarget.SymbolDescription)] string? symbol = null,
        [Description(SymbolTarget.FileDescription)] string? file = null,
        [Description(SymbolTarget.LineDescription)] int? line = null,
        [Description(SymbolTarget.CharacterDescription)] int? character = null,
        [Description(ContextDescription)] bool context = false,
        [Description(InheritedDescription)] bool inherited = false,
        [Description(Paging.LimitDescription)]
        [Range(1, Paging.MaximumLimit)]
            int limit = Paging.DefaultLimit,
        [Description(Paging.OffsetDescription)] [Range(0, int.MaxValue)] int offset = 0,
        CancellationToken cancellationToken = default
    ) =>
        pipeline.RunAsync(
            handler,
            NavigateRequest.From(
                action.ToLspAction(),
                (symbol, file, line, character),
                inherited,
                context,
                new Paging(limit, offset)
            ),
            cancellationToken
        );
}
