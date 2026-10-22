// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Returns the saved text of each declaration of a symbol, with its documentation comment.
    /// Declarations that do not fit the response budget are left out and their lines counted.
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
            for (var line = first; line <= last; line++)
            {
                text.Append(lines[line - 1]).Append('\n');
            }

            // A part shows whole or not at all, so the text never stops inside a member
            if (text.Length > remaining)
            {
                omitted += last - first + 1;
                continue;
            }

            remaining -= text.Length;
            parts.Add(new SourcePart(part.ToItem(paths), first, last, text.ToString()));
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
        // A type has no callers, and its implementations are its subtypes
        LspAction[] actions = targetsType
            ? [LspAction.References, LspAction.Subtypes]
            : [LspAction.References, LspAction.Callers, LspAction.Implementation];

        return await SectionsAsync(
            [.. actions.Select(ActionName)],
            (index, token) => ImpactSectionAsync(request, actions[index], file, position, token),
            cancellationToken
        );
    }

    private async Task<Result<ToolResult>> ImpactSectionAsync(
        LspRequest request,
        LspAction action,
        string file,
        SourcePosition position,
        CancellationToken cancellationToken
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

        // Roslyn lists a member that nothing overrides as its own implementation
        return section.Value is Locations implementations && action is LspAction.Implementation
            ? Without(implementations, paths.RelativePath(paths.Resolve(file)), position)
            : section;
    }

    private static Locations Without(Locations locations, string file, SourcePosition position)
    {
        var files = locations
            .Files.Select(found =>
                string.Equals(found.File, file, StringComparison.Ordinal)
                    ? found with
                    {
                        Positions = [.. found.Positions.Where(other => other != position)],
                    }
                    : found
            )
            .Where(found => found.Positions.Count > 0)
            .ToList();
        var removed =
            locations.Files.Sum(found => found.Positions.Count)
            - files.Sum(found => found.Positions.Count);
        return locations with { Files = files, Total = locations.Total - removed };
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
            ? ToolError.InvalidRequest(
                string.Create(CultureInfo.InvariantCulture, $"No declaration contains line {line}.")
            )
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
