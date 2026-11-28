// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Symbols;
using RoslynBridge.Workspace;

namespace RoslynBridge.Features.Navigate;

internal sealed partial class NavigateHandler
{
    // Keeps the text within a 32,000-character response, with room for headers
    private const int MAXIMUM_SOURCE_CHARACTERS = 31_000;

    /// <summary>
    /// Declarations that do not fit the response budget are left out and counted.
    /// </summary>
    private async Task<Result<SourceParts>> SourceAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    )
    {
        var declarations = await DeclarationsAsync(request, cancellationToken);
        if (declarations.IsError(out var error, out var found))
            return error;

        var parts = new List<SourcePart>();
        var remaining = MAXIMUM_SOURCE_CHARACTERS;
        var omitted = 0;
        foreach (var part in found)
        {
            if (SourceLines.ReadLines(part.File).IsError(out var readError, out var lines))
                return readError;

            var last = Math.Min(part.LastLine, lines.Count);
            var first = DocumentationStart(lines, part.FirstLine);
            var text = new StringBuilder();
            for (var line = first; line <= last; line++)
            {
                text.Append(lines[line]).Append('\n');
            }

            // A part shows whole or not at all, so the text never stops inside a member
            if (text.Length > remaining)
            {
                omitted += last - first + 1;
                continue;
            }

            remaining -= text.Length;
            parts.Add(new SourcePart(part.ToItem(), first, last, text.ToString()));
        }

        return new SourceParts(parts, omitted);
    }

    /// <summary>
    /// With <see cref="NavigateRequest.Inherited"/>, the members of workspace base classes follow.
    /// </summary>
    private async Task<Result<TypeMembers>> MembersAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    )
    {
        var declarations = await DeclarationsAsync(request, cancellationToken);
        if (declarations.IsError(out var error, out var found))
            return error;

        var ownMembers = await resolver.MembersAsync(found, cancellationToken);
        if (ownMembers.IsError(out var membersError, out var own))
            return membersError;

        List<SymbolItem> members =
        [
            .. own.Select(member => member.ToItem() with { Name = member.Signature }),
        ];
        if (request.Inherited)
        {
            var inheritedMembers = await InheritedMembersAsync(found[0], own, cancellationToken);
            if (inheritedMembers.IsError(out var inheritedError, out var inherited))
                return inheritedError;

            members.AddRange(inherited);
        }

        return new TypeMembers(
            found[0].ToItem(),
            [.. request.Page.Slice(members)],
            members.Count,
            request.Page.NextOffset(members.Count)
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
        var positioned = await locator.DocumentPositionAsync(type.Location, cancellationToken);
        if (positioned.IsError(out var error, out var position))
            return error;

        var preparation = await session.PrepareTypeHierarchyAsync(position, cancellationToken);
        if (preparation.IsError(out var prepareError, out var prepared))
            return prepareError;

        var redeclared = own.Select(MemberKey).ToHashSet(StringComparer.Ordinal);
        var inherited = new List<SymbolItem>();
        var current = prepared.FirstOrDefault();
        while (current is not null)
        {
            var next = await BaseMembersAsync(current, cancellationToken);
            if (next.IsError(out var baseError, out var found))
                return baseError;

            if (found is null)
                break;

            foreach (var member in found.Members.Where(member => redeclared.Add(MemberKey(member))))
            {
                inherited.Add(
                    member.ToItem() with
                    {
                        Name = member.Signature,
                        Container = found.Name,
                    }
                );
            }

            current = found.Class;
        }

        return inherited;
    }

    /// <summary>
    /// Null when the hierarchy ends, with no base class in the workspace or none whose file is readable.
    /// </summary>
    private async Task<Result<BaseMembers?>> BaseMembersAsync(
        HierarchyItem item,
        CancellationToken cancellationToken
    )
    {
        var supertypes = await session.SupertypesAsync(item, cancellationToken);
        if (supertypes.IsError(out var error, out var found))
            return error;

        var baseClass = found.FirstOrDefault(supertype =>
            supertype.Symbol.Kind == LspSymbolKind.Class
        );
        if (baseClass is null)
            return (BaseMembers?)null;

        var baseParts = await BaseDeclarationsAsync(baseClass, cancellationToken);
        if (baseParts.IsError(out var partsError, out var parts))
            return partsError;

        if (parts.Count == 0)
            return (BaseMembers?)null;

        var inheritable = await resolver.InheritableMembersAsync(parts, cancellationToken);
        return inheritable.Map(members =>
            (BaseMembers?)new BaseMembers(baseClass, parts[0].Name, members)
        );
    }

    /// <summary>
    /// Empty when the file of the base class cannot be read.
    /// </summary>
    private async Task<Result<List<SymbolDeclaration>>> BaseDeclarationsAsync(
        HierarchyItem baseClass,
        CancellationToken cancellationToken
    )
    {
        var item = baseClass.Symbol;
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
            return declarationError;

        if (declaration is null)
            return new List<SymbolDeclaration>();

        var resolved = await resolver.ResolveAsync(
            declaration.Name,
            file: null,
            preferTypes: true,
            cancellationToken
        );
        if (!resolved.IsError(out var resolveError, out var parts))
            return parts;

        return resolveError.IsInfrastructureFailure
            ? resolveError
            : new List<SymbolDeclaration> { declaration };
    }

    private async Task<Result<List<SymbolDeclaration>>> DeclarationsAsync(
        NavigateRequest request,
        CancellationToken cancellationToken
    ) =>
        request.Target switch
        {
            NamedSymbol named => await resolver.ResolveAsync(
                named.Symbol,
                named.File,
                request.Lookup.PrefersTypes,
                cancellationToken
            ),
            DeclarationLine line => await DeclarationAtAsync(line, cancellationToken),
            SymbolAt or ResolvedAt => throw new UnreachableException(
                "Source and members take a declaration line."
            ),
        };

    private async Task<Result<List<SymbolDeclaration>>> DeclarationAtAsync(
        DeclarationLine target,
        CancellationToken cancellationToken
    )
    {
        if (paths.Resolve(target.File).IsError(out var error, out var file))
            return error;

        var declared = await resolver.DeclarationAtAsync(file, target.Line, cancellationToken);
        return declared switch
        {
            ToolError declarationError => declarationError,
            SymbolDeclaration declaration => new List<SymbolDeclaration> { declaration },
            null => ToolError.InvalidRequest(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No declaration contains line {target.Line}."
                )
            ),
        };
    }

    // An override or a hiding member has the name and parameters of the member it replaces
    private static string MemberKey(SymbolDeclaration member) =>
        SymbolName.WithoutType(member.Signature);

    // Documentation comments are trivia, so the declaration range starts after them
    private static int DocumentationStart(FileLines lines, int firstLine)
    {
        var line = firstLine;
        while (
            line > 1
            && lines[line - 1].AsSpan().TrimStart().StartsWith("///", StringComparison.Ordinal)
        )
        {
            line--;
        }

        return line;
    }

    /// <param name="Name">The qualified name of the base class, which contains the members.</param>
    private sealed record BaseMembers(
        HierarchyItem Class,
        string Name,
        IReadOnlyList<SymbolDeclaration> Members
    );
}
