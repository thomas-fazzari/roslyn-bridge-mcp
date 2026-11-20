// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Symbols;

/// <summary>
/// Workspace symbol search matches loosely, so it only selects candidate files. Their document symbols give the exact names.
/// </summary>
internal sealed partial class SymbolResolver(RoslynSession session, WorkspacePaths paths)
{
    internal const int MaximumCandidates = 20;

    /// <summary>
    /// A partial type has one declaration per part, and the first is where navigation starts.
    /// An ambiguous or unknown name returns an error that lists candidates.
    /// </summary>
    /// <param name="preferTypes">Drops the other matches when some are types.</param>
    public async Task<Result<List<SymbolDeclaration>>> ResolveAsync(
        string text,
        string? file,
        bool preferTypes,
        CancellationToken cancellationToken
    )
    {
        if (SymbolName.Parse(text) is not { } requested)
            return ToolError.InvalidRequest($"'{text}' is not a valid symbol name.");

        var name = SymbolName.BaseName(requested.Segments[^1]);
        List<WorkspaceMatch>? matches = null;
        IEnumerable<WorkspaceFilePath> files;
        if (file is null)
        {
            var candidates = await CandidateMatchesAsync(requested, cancellationToken);
            if (candidates.IsError(out var matchError, out matches))
                return matchError;

            files = FilesNamed(matches, name);
        }
        else
        {
            if (paths.Resolve(file).IsError(out var pathError, out var path))
                return pathError;

            files = [path];
        }

        var symbolFiles = await DocumentSymbolsAsync(files, cancellationToken);
        if (symbolFiles.IsError(out var documentsError, out var documents))
            return documentsError;

        var declarations = FindDeclarations(documents, requested).ToList();
        if (
            preferTypes
            && declarations.Exists(declaration => LspSymbolKinds.IsType(declaration.Kind))
        )
        {
            declarations.RemoveAll(declaration => !LspSymbolKinds.IsType(declaration.Kind));
        }

        var projects = await ProjectsByFileAsync(declarations, cancellationToken);
        if (projects.IsError(out var projectsError, out var projectsByFile))
            return projectsError;

        var symbols = DistinctSymbols(declarations, projectsByFile);
        if (symbols.Count == 1)
        {
            var symbol = symbols[0];
            return declarations
                .Where(declaration => IsPartOf(declaration, symbol, projectsByFile))
                .OrderBy(declaration => declaration != symbol)
                .ToList();
        }

        if (symbols.Count == 0)
            return await NotFoundAsync(text, requested, name, matches, cancellationToken);

        var advice = AmbiguityAdvice(symbols);
        return new ToolError(
            ErrorCodes.AmbiguousSymbol,
            symbols.Count > MaximumCandidates
                ? $"'{text}' matches {symbols.Count} symbols. The first {MaximumCandidates} are listed. {advice}"
                : $"'{text}' matches {symbols.Count} symbols. {advice}",
            Candidates(symbols)
        );
    }

    /// <summary>
    /// Same-named types in different projects are separate symbols.
    /// </summary>
    internal static List<SymbolDeclaration> DistinctSymbols(
        IEnumerable<SymbolDeclaration> declarations,
        IReadOnlyDictionary<WorkspaceFilePath, HashSet<string>> projectsByFile
    )
    {
        var symbols = new List<SymbolDeclaration>();
        foreach (
            var group in declarations.GroupBy(
                declaration => declaration.Name,
                StringComparer.Ordinal
            )
        )
        {
            var representatives = new List<SymbolDeclaration>();
            foreach (var declaration in group)
            {
                if (
                    !representatives.Exists(representative =>
                        IsPartOf(declaration, representative, projectsByFile)
                    )
                )
                {
                    representatives.Add(declaration);
                }
            }

            symbols.AddRange(representatives);
        }

        return symbols;
    }

    /// <summary>
    /// Gives each item the name that <see cref="ResolveAsync"/> accepts. Items without a declaration in a workspace file keep Roslyn's name.
    /// </summary>
    public async Task<Result<List<SymbolItem>>> NameAsync(
        IReadOnlyList<SymbolItem> items,
        CancellationToken cancellationToken
    )
    {
        var locations = items.Select(DeclarationLocation).ToList();
        var declared = await DeclarationsAtAsync(
            locations.OfType<SourceLocation>(),
            cancellationToken
        );
        return declared.Map(declarations =>
            items
                .Zip(locations, (item, location) =>
                    location is { } found && declarations.TryGetValue(found, out var declaration)
                        ? item with
                        {
                            Name = declaration.Name,
                            Detail = null,
                        }
                        : item
                )
                .ToList()
        );
    }

    /// <summary>
    /// Returns the innermost declaration that contains the line, or null.
    /// </summary>
    public async Task<Result<SymbolDeclaration?>> DeclarationAtAsync(
        WorkspaceFilePath file,
        int line,
        CancellationToken cancellationToken
    ) =>
        (await DocumentSymbolsAsync([file], cancellationToken)).Map(documents =>
            Declarations(documents[0].Symbols, file, container: null, containerDisplay: null)
                .Select(declared => declared.Declaration)
                .Where(declaration => declaration.FirstLine <= line && line <= declaration.LastLine)
                .MinBy(declaration => declaration.LastLine - declaration.FirstLine)
        );

    /// <summary>
    /// Members of extension blocks count as the type's own.
    /// </summary>
    public async Task<Result<List<SymbolDeclaration>>> MembersAsync(
        IReadOnlyList<SymbolDeclaration> parts,
        CancellationToken cancellationToken
    ) =>
        (await DeclaredMembersAsync(parts, cancellationToken)).Map(members =>
            members.Select(declared => declared.Declaration).ToList()
        );

    /// <summary>
    /// Excludes private members, constructors and finalizers.
    /// </summary>
    public async Task<Result<List<SymbolDeclaration>>> InheritableMembersAsync(
        IReadOnlyList<SymbolDeclaration> parts,
        CancellationToken cancellationToken
    ) =>
        (await DeclaredMembersAsync(parts, cancellationToken)).Map(members =>
            members
                .Where(declared =>
                    !declared.Declaration.IsPrivate
                    && !declared.IsConstructor
                    && !declared.Declaration.Signature.StartsWith('~')
                )
                .Select(declared => declared.Declaration)
                .ToList()
        );

    /// <summary>
    /// Exact matches win over loose ones. A constructor needs its parameter list in the request.
    /// </summary>
    internal static IEnumerable<SymbolDeclaration> FindDeclarations(
        IEnumerable<(WorkspaceFilePath File, LspSymbol[] Symbols)> documents,
        SymbolName requested
    )
    {
        var candidates = documents
            .SelectMany(document =>
                Declarations(
                    document.Symbols,
                    document.File,
                    container: null,
                    containerDisplay: null
                )
            )
            .Where(declared => !declared.IsConstructor || requested.Parameters is not null)
            .ToList();
        var exact = candidates
            .Where(declared => requested.Selects(declared.Name, exact: true))
            .ToList();
        var selected =
            exact.Count > 0
                ? exact
                : candidates.Where(declared => requested.Selects(declared.Name, exact: false));

        return selected.Select(declared => declared.Declaration);
    }

    private static string AmbiguityAdvice(List<SymbolDeclaration> symbols)
    {
        var names = symbols.DistinctBy(symbol => symbol.Name, StringComparer.Ordinal).Count();
        if (names == symbols.Count)
            return "Send one candidate symbol instead.";

        var namesInFiles = symbols.DistinctBy(symbol => (symbol.Name, symbol.File)).Count();
        return namesInFiles == symbols.Count
            ? "Send one candidate symbol with its file."
            : "Send the file, line and character of one candidate instead.";
    }

    // An explicit interface implementation or a static constructor is private,
    // unlike the same-named member beside it, so accessibility tells them apart
    private static bool IsPartOf(
        SymbolDeclaration declaration,
        SymbolDeclaration symbol,
        IReadOnlyDictionary<WorkspaceFilePath, HashSet<string>> projectsByFile
    ) =>
        string.Equals(declaration.Name, symbol.Name, StringComparison.Ordinal)
        && declaration.IsPrivate == symbol.IsPrivate
        && SharesProject(declaration.File, symbol.File, projectsByFile);

    // Without known projects, same-named declarations count as parts of one partial type
    private static bool SharesProject(
        WorkspaceFilePath first,
        WorkspaceFilePath second,
        IReadOnlyDictionary<WorkspaceFilePath, HashSet<string>> projectsByFile
    ) =>
        first == second
        || !projectsByFile.TryGetValue(first, out var firstProjects)
        || !projectsByFile.TryGetValue(second, out var secondProjects)
        || firstProjects.Count == 0
        || secondProjects.Count == 0
        || firstProjects.Overlaps(secondProjects);

    private async Task<Result<Dictionary<WorkspaceFilePath, HashSet<string>>>> ProjectsByFileAsync(
        List<SymbolDeclaration> declarations,
        CancellationToken cancellationToken
    )
    {
        var projectsByFile = new Dictionary<WorkspaceFilePath, HashSet<string>>();
        var sharedNames = declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .Where(group => group.DistinctBy(declaration => declaration.File).Skip(1).Any());
        foreach (var file in sharedNames.SelectMany(group => group).Select(d => d.File).Distinct())
        {
            var opened = await session.OpenDocumentAsync(file, cancellationToken);
            if (opened.IsError(out var openError, out var uri))
                return openError;

            var contextLookup = await session.ProjectContextsAsync(uri, cancellationToken);
            if (contextLookup.IsError(out var contextsError, out var contexts))
                return contextsError;

            projectsByFile[file] = contexts
                .Select(context => context.Id)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }

        return projectsByFile;
    }

    private async Task<Result<IEnumerable<DeclaredSymbol>>> DeclaredMembersAsync(
        IReadOnlyList<SymbolDeclaration> parts,
        CancellationToken cancellationToken
    )
    {
        var type = SymbolName.Parse(parts[0].Name)!;
        return (
            await DocumentSymbolsAsync(
                parts.Select(part => part.File).Distinct().Order(WorkspaceFilePath.Order),
                cancellationToken
            )
        ).Map(documents =>
            documents
                .SelectMany(document =>
                    Declarations(
                        document.Symbols,
                        document.File,
                        container: null,
                        containerDisplay: null
                    )
                )
                .Where(declared =>
                    declared.Name.Segments.Length == type.Segments.Length + 1
                    && declared
                        .Name.Segments[..type.Segments.Length]
                        .SequenceEqual(type.Segments, StringComparer.Ordinal)
                )
        );
    }

    // Opening a document changes the session, so only the symbol requests run concurrently
    private async Task<
        Result<List<(WorkspaceFilePath File, LspSymbol[] Symbols)>>
    > DocumentSymbolsAsync(
        IEnumerable<WorkspaceFilePath> files,
        CancellationToken cancellationToken
    )
    {
        var documents = new List<(WorkspaceFilePath File, LspSymbol[] Symbols)>();
        // Batches stay within the open documents the session keeps
        foreach (var batch in files.Chunk(RoslynConnection.MaximumOpenDocuments))
        {
            if (
                (await session.OpenDocumentsAsync(batch, cancellationToken)).IsError(
                    out var openError,
                    out var uris
                )
            )
            {
                return openError;
            }

            var responses = await Task.WhenAll(
                uris.Select(uri => session.DocumentSymbolsAsync(uri, cancellationToken))
            );
            if (responses.All().IsError(out var error, out var symbols))
                return error;

            documents.AddRange(batch.Zip(symbols));
        }

        return documents;
    }

    // Namespaces only qualify their members, so they are not declarations here
    private static IEnumerable<DeclaredSymbol> Declarations(
        LspSymbol[] symbols,
        WorkspaceFilePath file,
        SymbolName? container,
        string? containerDisplay
    )
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Name is not { Length: > 0 } rawName)
                continue;

            if (SymbolName.Parse(rawName) is not { } member)
                continue;

            var kind = symbol.Kind;
            var children = symbol.Children ?? [];
            // Members of an extension block are named as members of its enclosing class
            if (IsExtensionBlock(member, kind))
            {
                foreach (var declared in Declarations(children, file, container, containerDisplay))
                {
                    yield return declared;
                }

                continue;
            }

            var name = container is null ? member : container.Append(member);
            var display = SymbolName.WithoutType(rawName);
            display = containerDisplay is null ? display : $"{containerDisplay}.{display}";
            if (
                kind is not LspSymbolKind.Namespace
                && symbol is { SelectionRange: { } selection, Range: { } range }
            )
            {
                var start = selection.Start.OneBased;
                yield return new DeclaredSymbol(
                    name,
                    name.IsCtor,
                    new SymbolDeclaration(
                        display,
                        rawName.Trim(),
                        name.CorrectedKind(kind),
                        file,
                        start.Line,
                        start.Character,
                        range.Start.OneBased.Line,
                        range.End.OneBased.Line,
                        RoslynGlyphs.IsPrivate(symbol.Glyph)
                    )
                );
            }

            // A container's parameter list is not part of its members' names, as for local functions
            foreach (
                var declared in Declarations(
                    children,
                    file,
                    name with
                    {
                        Parameters = null,
                    },
                    SymbolName.WithoutParameters(display)
                )
            )
            {
                yield return declared;
            }
        }
    }

    private static SourceLocation? DeclarationLocation(SymbolItem item) =>
        item is { File: LocationFile.Workspace file, Position: { } position }
            ? new SourceLocation(file, position)
            : null;

    private static bool IsExtensionBlock(SymbolName member, LspSymbolKind kind) =>
        member is { Segments: [var block], Parameters: not null }
        && SymbolName.BaseName(block) is "extension"
        && kind is not LspSymbolKind.Method;

    private async Task<Result<Dictionary<SourceLocation, SymbolDeclaration>>> DeclarationsAtAsync(
        IEnumerable<SourceLocation> positions,
        CancellationToken cancellationToken
    )
    {
        var wanted = positions.ToHashSet();
        var symbolFiles = await DocumentSymbolsAsync(
            wanted
                .Select(position => position.File.WorkspaceFile)
                .OfType<WorkspaceFilePath>()
                .Distinct(),
            cancellationToken
        );
        if (symbolFiles.IsError(out var error, out var documents))
            return error;

        var found = new Dictionary<SourceLocation, SymbolDeclaration>();

        foreach (var (file, symbols) in documents)
        {
            foreach (
                var declared in Declarations(symbols, file, container: null, containerDisplay: null)
            )
            {
                var declaration = declared.Declaration;
                if (wanted.Contains(declaration.Location))
                    found.TryAdd(declaration.Location, declaration);
            }
        }

        return found;
    }

    private async Task<Result<List<WorkspaceMatch>>> CandidateMatchesAsync(
        SymbolName requested,
        CancellationToken cancellationToken
    )
    {
        var segments = requested.Segments.Select(SymbolName.BaseName).ToArray();
        // Roslyn matches a qualified query against containers, and an extension block member's
        // container is the block, so retry with the member name alone
        return await WorkspaceMatchesAsync(string.Join('.', segments), cancellationToken) switch
        {
            ToolError error => error,
            List<WorkspaceMatch> matches
                when segments.Length > 1
                    && !matches.Exists(match => IsNamed(match, segments[^1])) =>
                await WorkspaceMatchesAsync(segments[^1], cancellationToken),
            List<WorkspaceMatch> found => found,
        };
    }

    private async Task<Result<List<WorkspaceMatch>>> WorkspaceMatchesAsync(
        string query,
        CancellationToken cancellationToken
    )
    {
        var response = await session.WorkspaceSymbolsAsync(query, cancellationToken);
        if (response.IsError(out var error, out var results))
            return error;

        var matches = new List<WorkspaceMatch>();
        // Resolving a file checks each folder on disk, and a file declares many symbols
        var files = new Dictionary<string, WorkspaceFilePath?>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (
                SymbolName.Parse(result.Name) is { } found
                && result.Location is { Uri: { } uri, Range: { } range }
                && WorkspaceFile(uri) is { } file
            )
            {
                matches.Add(
                    new WorkspaceMatch(
                        SymbolName.BaseName(found.Segments[^1]),
                        file,
                        range.Start.OneBased,
                        result.Kind
                    )
                );
            }
        }

        return matches;

        WorkspaceFilePath? WorkspaceFile(string uri)
        {
            if (!files.TryGetValue(uri, out var file))
            {
                file = paths.TryGetWorkspaceFile(uri);
                files.Add(uri, file);
            }

            return file;
        }
    }

    private static bool IsNamed(WorkspaceMatch match, string name) =>
        string.Equals(match.Name, name, StringComparison.Ordinal);

    private static IEnumerable<WorkspaceFilePath> FilesNamed(
        IEnumerable<WorkspaceMatch> matches,
        string name
    ) =>
        matches
            .Where(match => IsNamed(match, name))
            .Select(match => match.File)
            .Distinct()
            .Order(WorkspaceFilePath.Order);

    private static List<SymbolItem> Candidates(IEnumerable<SymbolDeclaration> declarations) =>
        [
            .. declarations
                .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.File, WorkspaceFilePath.Order)
                .Take(MaximumCandidates)
                .Select(declaration => declaration.ToItem()),
        ];

    private readonly record struct DeclaredSymbol(
        SymbolName Name,
        bool IsConstructor,
        SymbolDeclaration Declaration
    );
}
