// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
    /// Returns the members declared in a type, across its partial declarations. With
    /// <see cref="LspRequest.Inherited"/>, the members that its base classes in the workspace pass on follow.
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

        var own = await resolver.MembersAsync(found.Value, cancellationToken);
        List<SymbolItem> members =
        [
            .. own.Select(member => member.ToItem(paths) with { Name = member.Signature }),
        ];
        if (request.Inherited)
        {
            members.AddRange(await InheritedMembersAsync(found.Value[0], own, cancellationToken));
        }

        return new TypeMembers(
            found.Value[0].ToItem(paths),
            [.. members.Skip(request.Offset).Take(request.Limit)],
            members.Count,
            LspRequest.NextOffset(request.Offset, request.Limit, members.Count)
        );
    }

    /// <returns>
    /// The members that the base classes of the type in the workspace pass on, nearest base first, with the
    /// base class as their container. A member that a nearer type redeclares, such as an override, is left out.
    /// </returns>
    private async Task<List<SymbolItem>> InheritedMembersAsync(
        SymbolDeclaration type,
        IReadOnlyList<SymbolDeclaration> own,
        CancellationToken cancellationToken
    )
    {
        var redeclared = own.Select(MemberKey).ToHashSet(StringComparer.Ordinal);
        var inherited = new List<SymbolItem>();
        var prepared = await session.RequestAsync(
            LspMethods.TextDocumentPrepareTypeHierarchy,
            await PositionParametersAsync(
                type.File,
                new SourcePosition(type.Line, type.Character),
                cancellationToken
            ),
            cancellationToken
        );
        var current = (prepared as JsonArray)?.FirstOrDefault();
        while (
            current is not null && await BaseClassAsync(current, cancellationToken) is { } baseClass
        )
        {
            var parts = await BaseDeclarationsAsync(baseClass, cancellationToken);
            if (parts.Count == 0)
            {
                break;
            }

            foreach (var member in await resolver.InheritableMembersAsync(parts, cancellationToken))
            {
                if (redeclared.Add(MemberKey(member)))
                {
                    inherited.Add(
                        member.ToItem(paths) with
                        {
                            Name = member.Signature,
                            Container = parts[0].Name,
                        }
                    );
                }
            }

            current = baseClass;
        }

        return inherited;
    }

    /// <returns>The base class of a type hierarchy item, or null when Roslyn reports none in the workspace.</returns>
    private async Task<JsonNode?> BaseClassAsync(JsonNode item, CancellationToken cancellationToken)
    {
        var supertypes = await session.RequestAsync(
            LspMethods.TypeHierarchySupertypes,
            new JsonObject { ["item"] = item.DeepClone() },
            cancellationToken
        );
        return (supertypes as JsonArray ?? [])
            .FirstOrDefault(supertype =>
                supertype?["kind"]?.GetValue<int>() is LspSymbolKinds.Class
            )
            ?.DeepClone();
    }

    /// <returns>The declarations of a base class, one per partial part, or none when its file cannot be read.</returns>
    private async Task<IReadOnlyList<SymbolDeclaration>> BaseDeclarationsAsync(
        JsonNode baseClass,
        CancellationToken cancellationToken
    )
    {
        if (
            baseClass["uri"]?.GetValue<string>() is not { } uri
            || paths.TryGetWorkspaceFile(uri) is not { } file
            || baseClass["selectionRange"]?["start"]?["line"]?.GetValue<int>() is not { } line
            || await resolver.DeclarationAtAsync(file, line + 1, cancellationToken)
                is not { } declaration
        )
        {
            return [];
        }

        var parts = await resolver.ResolveAsync(declaration.Name, file: null, cancellationToken);
        return parts.Error is null ? parts.Value : [declaration];
    }

    // An override or a hiding member has the name and parameters of the member it replaces
    private static string MemberKey(SymbolDeclaration member) =>
        SymbolName.WithoutType(member.Signature);

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
            [.. actions.Select(action => action.Name)],
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
