// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Features.BulkNavigate;
using RoslynBridge.Features.Diagnostics;
using RoslynBridge.Features.Edit;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Features.Server;
using RoslynBridge.Features.Symbols;
using RoslynBridge.Requests;

namespace RoslynBridge.Tests;

/// <summary>
/// The arguments of one tool call, with the action naming the tool that owns it.
/// </summary>
internal sealed record LspRequest
{
    public required LspAction Action { get; init; }

    public string? File { get; init; }

    public string? Symbol { get; init; }

    public int? Line { get; init; }

    public int? Character { get; init; }

    public int? EndLine { get; init; }

    public int? EndCharacter { get; init; }

    public string? Query { get; init; }

    public string? NewName { get; init; }

    public string? Diagnostic { get; init; }

    public int? ActionIndex { get; init; }

    public bool Apply { get; init; }

    public bool All { get; init; }

    public bool Context { get; init; }

    public bool Inherited { get; init; }

    public bool Unused { get; init; }

    public string? ProposalId { get; init; }

    public string? Method { get; init; }

    public JsonObject? Parameters { get; init; }

    public IReadOnlyList<LspRequest> Queries { get; init; } = [];

    /// <summary>
    /// Without it, the diagnostics tool uses the project setting.
    /// </summary>
    public DiagnosticSeverity? Severity { get; init; }

    public IReadOnlyList<string>? Ids { get; init; }

    public bool Summary { get; init; }

    public int Limit { get; init; } = Paging.DefaultLimit;

    public int Offset { get; init; }

    public bool IsNavigation => Enum.TryParse<NavigationAction>(Action.ToString(), out _);

    public bool IsEdit => Enum.TryParse<EditAction>(Action.ToString(), out _);

    // The tool that owns the action, with flat arguments, as an agent would call it
    public (string Tool, Dictionary<string, object?> Arguments) ToolCall()
    {
        var (tool, parameters) = Action switch
        {
            LspAction.Diagnostics => (
                DiagnosticsTool.Name,
                ["file", "severity", "ids", "summary", "limit", "offset"]
            ),
            LspAction.Symbols => (SymbolsTool.Name, ["file", "query", "unused", "limit", "offset"]),
            _ when IsNavigation => (
                NavigateTool.Name,
                new[]
                {
                    "action",
                    "symbol",
                    "file",
                    "line",
                    "character",
                    "context",
                    "inherited",
                    "limit",
                    "offset",
                }
            ),
            LspAction.Bulk => (BulkNavigateTool.Name, ["queries", "context", "limit", "offset"]),
            _ when IsEdit => (
                EditTool.Name,
                [
                    "action",
                    "symbol",
                    "file",
                    "line",
                    "character",
                    "endLine",
                    "endCharacter",
                    "newName",
                    "diagnostic",
                    "actionIndex",
                    "proposalId",
                    "apply",
                    "all",
                    "limit",
                ]
            ),
            _ => (ServerTool.Name, ["action", "method", "parameters", "apply"]),
        };

        return (tool, Arguments(parameters));
    }

    private Dictionary<string, object?> Arguments(string[] names)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(Action.ToString()),
            ["file"] = File,
            ["symbol"] = Symbol,
            ["severity"] = Severity?.ToString().ToLowerInvariant(),
            ["ids"] = Ids,
            ["summary"] = Summary ? true : null,
            ["line"] = Line,
            ["character"] = Character,
            ["endLine"] = EndLine,
            ["endCharacter"] = EndCharacter,
            ["query"] = Query,
            ["newName"] = NewName,
            ["diagnostic"] = Diagnostic,
            ["actionIndex"] = ActionIndex,
            ["proposalId"] = ProposalId,
            ["method"] = Method,
            ["parameters"] = Parameters,
            ["apply"] = Apply ? true : null,
            ["all"] = All ? true : null,
            ["context"] = Context ? true : null,
            ["inherited"] = Inherited ? true : null,
            ["unused"] = Unused ? true : null,
            ["limit"] = Limit,
            ["offset"] = Offset,
            ["queries"] =
                Queries.Count > 0
                    ? Queries
                        .Select(query =>
                            query.Arguments([
                                "action",
                                "symbol",
                                "file",
                                "line",
                                "character",
                                "inherited",
                            ])
                        )
                        .ToList()
                    : null,
        };

        return names
            .Where(name => values[name] is not null)
            .ToDictionary(name => name, name => values[name], StringComparer.Ordinal);
    }
}
