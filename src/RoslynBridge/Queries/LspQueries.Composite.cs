// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

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
    /// Declarations that do not fit the response budget are left out and counted.
    /// </summary>
    public async Task<Result<SourceParts>> SourceAsync(
        NavigationRequest request,
        CancellationToken cancellationToken
    )
    {
        var declarations = await DeclarationsAsync(request, cancellationToken);
        if (declarations.IsError(out var error, out var found))
        {
            return error;
        }

        var parts = new List<SourcePart>();
        var remaining = MaximumSourceCharacters;
        var omitted = 0;
        foreach (var part in found)
        {
            if (SourceLines.ReadLines(part.File).IsError(out var readError, out var lines))
            {
                return readError;
            }

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
    /// With <see cref="NavigationRequest.Inherited"/>, the members of workspace base classes follow.
    /// </summary>
    public async Task<Result<TypeMembers>> MembersAsync(
        NavigationRequest request,
        CancellationToken cancellationToken
    )
    {
        var declarations = await DeclarationsAsync(request, cancellationToken);
        if (declarations.IsError(out var error, out var found))
        {
            return error;
        }

        var ownMembers = await resolver.MembersAsync(found, cancellationToken);
        if (ownMembers.IsError(out var membersError, out var own))
        {
            return membersError;
        }

        List<SymbolItem> members =
        [
            .. own.Select(member => member.ToItem(paths) with { Name = member.Signature }),
        ];
        if (request.Inherited)
        {
            var inheritedMembers = await InheritedMembersAsync(found[0], own, cancellationToken);
            if (inheritedMembers.IsError(out var inheritedError, out var inherited))
            {
                return inheritedError;
            }

            members.AddRange(inherited);
        }

        return new TypeMembers(
            found[0].ToItem(paths),
            [.. members.Skip(request.Page.Offset).Take(request.Page.Limit)],
            members.Count,
            Paging.NextOffset(request.Page.Offset, request.Page.Limit, members.Count)
        );
    }

    /// <summary>
    /// Nearest base first, with the base class as container. A member that a nearer type redeclares is left out.
    /// </summary>
    private async Task<Result<List<SymbolItem>>> InheritedMembersAsync(
        SymbolDeclaration type,
        IReadOnlyList<SymbolDeclaration> own,
        CancellationToken cancellationToken
    )
    {
        var redeclared = own.Select(MemberKey).ToHashSet(StringComparer.Ordinal);
        var inherited = new List<SymbolItem>();
        var positioned = await PositionParametersAsync(
            type.File,
            new SourcePosition(type.Line, type.Character),
            cancellationToken
        );
        if (positioned.IsError(out var error, out var parameters))
        {
            return error;
        }

        var preparation = await session.RequestAsync(
            LspMethods.TextDocumentPrepareTypeHierarchy,
            parameters,
            cancellationToken
        );
        if (preparation.IsError(out var prepareError, out var prepared))
        {
            return prepareError;
        }

        var current = (prepared as JsonArray)?.FirstOrDefault();
        while (current is not null)
        {
            var baseResult = await BaseClassAsync(current, cancellationToken);
            if (baseResult.IsError(out var baseError, out var baseClass))
            {
                return baseError;
            }

            if (baseClass is null)
            {
                break;
            }

            var baseParts = await BaseDeclarationsAsync(baseClass, cancellationToken);
            if (baseParts.IsError(out var partsError, out var parts))
            {
                return partsError;
            }

            if (parts.Count == 0)
            {
                break;
            }

            var inheritableMembers = await resolver.InheritableMembersAsync(
                parts,
                cancellationToken
            );
            if (inheritableMembers.IsError(out var inheritableError, out var inheritable))
            {
                return inheritableError;
            }

            foreach (var member in inheritable)
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

    /// <summary>
    /// Null when Roslyn reports no base class in the workspace.
    /// </summary>
    private async Task<Result<JsonNode?>> BaseClassAsync(
        JsonNode item,
        CancellationToken cancellationToken
    )
    {
        var response = await session.RequestAsync(
            LspMethods.TypeHierarchySupertypes,
            new JsonObject { ["item"] = item.DeepClone() },
            cancellationToken
        );
        if (response.IsError(out var error, out var supertypes))
        {
            return error;
        }

        foreach (var supertype in supertypes as JsonArray ?? [])
        {
            if (supertype.Read<LspSymbol>().IsError(out var invalid, out var symbol))
            {
                return invalid;
            }

            if (symbol.Kind == LspSymbolKinds.Class)
            {
                return supertype!.DeepClone();
            }
        }

        return (JsonNode?)null;
    }

    /// <summary>
    /// Empty when the file of the base class cannot be read.
    /// </summary>
    private async Task<Result<List<SymbolDeclaration>>> BaseDeclarationsAsync(
        JsonNode baseClass,
        CancellationToken cancellationToken
    )
    {
        if (baseClass.Read<LspSymbol>().IsError(out var error, out var item))
        {
            return error;
        }

        if (
            item.Uri is not { } uri
            || paths.TryGetWorkspaceFile(uri) is not { } file
            || item.SelectionRange?.Start.OneBased.Line is not { } line
        )
        {
            return new List<SymbolDeclaration>();
        }

        var declared = await resolver.DeclarationAtAsync(file, line, cancellationToken);
        if (declared.IsError(out var declarationError, out var declaration))
        {
            return declarationError;
        }

        if (declaration is null)
        {
            return new List<SymbolDeclaration>();
        }

        var resolved = await resolver.ResolveAsync(
            declaration.Name,
            file: null,
            preferTypes: true,
            cancellationToken
        );
        if (!resolved.IsError(out var resolveError, out var parts))
        {
            return parts;
        }

        if (resolveError.IsInfrastructureFailure)
        {
            return resolveError;
        }

        return new List<SymbolDeclaration> { declaration };
    }

    // An override or a hiding member has the name and parameters of the member it replaces
    private static string MemberKey(SymbolDeclaration member) =>
        SymbolName.WithoutType(member.Signature);

    private async Task<Result<ToolResult>> ImpactAsync(
        NavigationRequest request,
        CancellationToken cancellationToken
    )
    {
        var located = await LocateAsync(request.Target, request.Action, cancellationToken);
        if (located.IsError(out var error, out var location))
        {
            return error;
        }

        var (file, position) = location;
        var positioned = await PositionParametersAsync(file, position, cancellationToken);
        if (positioned.IsError(out var parametersError, out var parameters))
        {
            return parametersError;
        }

        var targeted = await TargetsTypeAsync(parameters, cancellationToken);
        if (targeted.IsError(out var typeError, out var targetsType))
        {
            return typeError;
        }

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
        NavigationRequest request,
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
                Target = SymbolTarget.At(file, position),
            },
            cancellationToken
        );

        // Roslyn lists a member that nothing overrides as its own implementation
        if (action is not LspAction.Implementation || section is not Locations implementations)
        {
            return section;
        }

        if (paths.Resolve(file).IsError(out var error, out var path))
        {
            return error;
        }

        return Without(implementations, paths.RelativePath(path), position);
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
    private async Task<Result<bool>> TargetsTypeAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        var response = await session.RequestAsync(
            LspMethods.TextDocumentPrepareTypeHierarchy,
            parameters,
            cancellationToken
        );
        if (response.IsError(out var error, out var prepared))
        {
            return error;
        }

        var symbols = (prepared as JsonArray ?? [])
            .Select(static item => item.Read<LspSymbol>())
            .All();
        if (symbols.IsError(out var invalid, out var items))
        {
            return invalid;
        }

        var file = paths.TryGetWorkspaceFile(
            parameters["textDocument"]!["uri"]!.GetValue<string>()
        );
        if (
            parameters["position"]
                .Read<LspPosition>()
                .IsError(out var positionError, out var position)
        )
        {
            return positionError;
        }

        // Roslyn can write the URI it received in another form, such as file:///c%3A/ for file:///C:/
        return items.Any(item =>
            item.Uri is { } uri
            && paths.TryGetWorkspaceFile(uri) is { } itemFile
            && string.Equals(itemFile, file, StringComparison.Ordinal)
            && item.SelectionRange is { } range
            && range.Contains(position)
        );
    }

    // A request names the symbol, or gives a line inside its declaration
    private async Task<Result<List<SymbolDeclaration>>> DeclarationsAsync(
        NavigationRequest request,
        CancellationToken cancellationToken
    )
    {
        var target = request.Target;
        if (target.Symbol is not null)
        {
            return target is { Line: null, Character: null }
                ? await resolver.ResolveAsync(
                    target.Symbol,
                    target.File,
                    request.Action.TakesType,
                    cancellationToken
                )
                : ToolError.InvalidRequest(
                    "Pass symbol with an optional file, or a file with a line."
                );
        }

        if (target is not { File: not null, Line: > 0 and var line })
        {
            return ToolError.InvalidRequest("Provide symbol, or a file with a one-based line.");
        }

        if (paths.Resolve(target.File).IsError(out var error, out var file))
        {
            return error;
        }

        var declared = await resolver.DeclarationAtAsync(file, line, cancellationToken);
        return declared switch
        {
            ToolError declarationError => declarationError,
            SymbolDeclaration declaration => new List<SymbolDeclaration> { declaration },
            null => ToolError.InvalidRequest(
                string.Create(CultureInfo.InvariantCulture, $"No declaration contains line {line}.")
            ),
        };
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
