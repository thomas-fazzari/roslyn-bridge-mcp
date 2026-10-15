// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    // Keeps the text within a 32,000-character response, with room for headers
    private const int MaximumSourceCharacters = 31_000;

    private static readonly LspAction[] _impactActions =
    [
        LspAction.References,
        LspAction.Callers,
        LspAction.Implementation,
        LspAction.Subtypes,
    ];

    /// <summary>
    /// Returns the saved text of each declaration of a symbol, with its documentation comment.
    /// Lines past the response budget are only counted.
    /// </summary>
    public async Task<Result<SourceParts>> SourceAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var found = await DeclarationsAsync(request, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var parts = new List<SourcePart>();
        var remaining = MaximumSourceCharacters;
        var omitted = 0;
        foreach (var part in found.Value)
        {
            var lines = SourceLines.ReadLines(part.File);
            var last = Math.Min(part.LastLine, lines.Length);
            var first = DocumentationStart(lines, part.FirstLine);
            var text = new StringBuilder();
            var line = first;
            for (; line <= last && lines[line - 1].Length < remaining; line++)
            {
                text.Append(lines[line - 1]).Append('\n');
                remaining -= lines[line - 1].Length + 1;
            }

            omitted += last - line + 1;
            if (text.Length == 0)
            {
                continue;
            }

            parts.Add(new SourcePart(part.ToItem(paths), first, line - 1, text.ToString()));
        }

        return new SourceParts(parts, omitted);
    }

    /// <summary>
    /// Returns the members declared in a type, across its partial declarations.
    /// </summary>
    public async Task<Result<TypeMembers>> MembersAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var found = await DeclarationsAsync(request, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var members = await resolver.MembersAsync(found.Value, cancellationToken);
        return new TypeMembers(
            found.Value[0].ToItem(paths),
            [
                .. members
                    .Take(request.Limit)
                    .Select(member => member.ToItem(paths) with { Name = member.Signature }),
            ],
            members.Count,
            members.Count > request.Limit
        );
    }

    /// <summary>
    /// Runs the impact navigations on the declaration the request targets, which is resolved only once.
    /// </summary>
    private async Task<Result<ToolResult>> ImpactAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var location = await LocateAsync(request, cancellationToken);
        if (location.Error is { } error)
        {
            return error;
        }

        var (file, position) = location.Value;
        var targetsType = await TargetsTypeAsync(
            await PositionParametersAsync(file, position, cancellationToken),
            cancellationToken
        );
        var sections = new List<ImpactSection>(_impactActions.Length);
        foreach (
            var action in _impactActions.Where(action =>
                targetsType || action is not LspAction.Subtypes
            )
        )
        {
            var section = await NavigationAsync(
                request with
                {
                    Action = action,
                    Symbol = null,
                    File = file,
                    Line = position.Line,
                    Character = position.Character,
                },
                cancellationToken
            );
            if (section.Error is { } sectionError)
            {
                return sectionError;
            }

            sections.Add(
                new ImpactSection(
                    JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString()),
                    section.Value
                )
            );
        }

        return new ImpactReport(sections);
    }

    // For a member, Roslyn prepares the hierarchy of its containing type, whose name lies elsewhere
    private async Task<bool> TargetsTypeAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        var items =
            await session.RequestAsync(
                LspMethods.TextDocumentPrepareTypeHierarchy,
                parameters,
                cancellationToken
            ) as JsonArray
            ?? [];
        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        var position = parameters["position"]!;
        return items.Any(item =>
            string.Equals(item?["uri"]?.GetValue<string>(), uri, StringComparison.Ordinal)
            && item?["selectionRange"] is { } range
            && IsAtOrBefore(range["start"]!, position)
            && IsAtOrBefore(position, range["end"]!)
        );
    }

    private static bool IsAtOrBefore(JsonNode first, JsonNode second)
    {
        var (line, otherLine) = (first["line"]!.GetValue<int>(), second["line"]!.GetValue<int>());
        return line < otherLine
            || (
                line == otherLine
                && first["character"]!.GetValue<int>() <= second["character"]!.GetValue<int>()
            );
    }

    // A request names the symbol, or gives a line inside its declaration
    private async Task<Result<IReadOnlyList<SymbolDeclaration>>> DeclarationsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Symbol is not null)
        {
            return request is { Line: null, Character: null }
                ? await resolver.ResolveAsync(request.Symbol, request.File, cancellationToken)
                : ToolError.InvalidRequest(
                    "Pass symbol with an optional file, or a file with a line."
                );
        }

        if (request is not { File: not null, Line: > 0 and var line })
        {
            return ToolError.InvalidRequest("Provide symbol, or a file with a one-based line.");
        }

        var declaration = await resolver.DeclarationAtAsync(
            paths.Resolve(request.File),
            line,
            cancellationToken
        );
        return declaration is null
            ? ToolError.InvalidRequest($"No declaration contains line {line}.")
            : new[] { declaration };
    }

    // Documentation comments are trivia, so the declaration range starts after them
    private static int DocumentationStart(string[] lines, int firstLine)
    {
        var line = firstLine;
        while (
            line > 1
            && lines[line - 2].AsSpan().TrimStart().StartsWith("///", StringComparison.Ordinal)
        )
        {
            line--;
        }

        return line;
    }
}
