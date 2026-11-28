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
    private const string ACTION_DESCRIPTION = "Navigation to perform.";
    private const string CONTEXT_DESCRIPTION =
        "Show the trimmed source line of each result position. Uses more tokens.";
    private const string INHERITED_DESCRIPTION =
        "For members, also list the members that base classes in the workspace pass on, after the type's own.";

    public static string Name => "navigate";

    public static string Description =>
        "Find the definition, type definition, implementations, references, callers, callees, supertypes or subtypes of a C# symbol, read its hover information or source, read the compiler facts of the expression or declared name at a position (type, conversion, constant, chosen overload), list a type's members, gather the impact of changing it, or list the reads or writes of a field, property, local, parameter or event. Name the symbol, or give its one-based position.";

    public static bool ReadOnly => true;

    public Task<CallToolResult> NavigateAsync(
        [Description(ACTION_DESCRIPTION)] NavigationAction action,
        [Description(SymbolTarget.SYMBOL_DESCRIPTION)] string? symbol = null,
        [Description(SymbolTarget.FILE_DESCRIPTION)] string? file = null,
        [Description(SymbolTarget.LINE_DESCRIPTION)] int? line = null,
        [Description(SymbolTarget.CHARACTER_DESCRIPTION)] int? character = null,
        [Description(CONTEXT_DESCRIPTION)] bool context = false,
        [Description(INHERITED_DESCRIPTION)] bool inherited = false,
        [Description(Paging.LIMIT_DESCRIPTION)]
        [Range(1, Paging.MAXIMUM_LIMIT)]
            int limit = Paging.DEFAULT_LIMIT,
        [Description(Paging.OFFSET_DESCRIPTION)] [Range(0, int.MaxValue)] int offset = 0,
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
