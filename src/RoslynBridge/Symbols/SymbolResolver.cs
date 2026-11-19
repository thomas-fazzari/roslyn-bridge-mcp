// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Fastenshtein;
using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Symbols;

/// <summary>
/// Workspace symbol search matches loosely, so it only selects candidate files. Their document symbols give the exact names.
/// </summary>
internal sealed class SymbolResolver(RoslynSession session, WorkspacePaths paths)
{
    internal const int MaximumCandidates = 20;
    private const int MaximumSuggestions = 10;
    private const int MaximumTypoDistance = 3;

    // One shared word, such as Get, matches too many members
    private const int MinimumSharedWords = 2;

    private static readonly Result<ContainerLookup?> _noContainer = (ContainerLookup?)null;

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
        {
            return ToolError.InvalidRequest($"'{text}' is not a valid symbol name.");
        }

        var name = SymbolName.BaseName(requested.Segments[^1]);
        List<WorkspaceMatch>? matches = null;
        IEnumerable<string> files;
        if (file is null)
        {
            var candidates = await CandidateMatchesAsync(requested, cancellationToken);
            if (candidates.IsError(out var matchError, out matches))
            {
                return matchError;
            }

            files = FilesNamed(matches, name);
        }
        else
        {
            if (paths.Resolve(file).IsError(out var pathError, out var path))
            {
                return pathError;
            }

            files = [path];
        }

        var symbolFiles = await DocumentSymbolsAsync(files, cancellationToken);
        if (symbolFiles.IsError(out var documentsError, out var documents))
        {
            return documentsError;
        }

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
        {
            return projectsError;
        }

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
        {
            return await NotFoundAsync(text, requested, name, matches, cancellationToken);
        }

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
        IReadOnlyDictionary<string, HashSet<string>> projectsByFile
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
    /// Returns names in the format <see cref="ResolveAsync"/> accepts. Positions without a declaration are omitted.
    /// </summary>
    public async Task<
        Result<Dictionary<(string File, SourcePosition Position), string>>
    > NamesAsync(
        IEnumerable<(string File, SourcePosition Position)> positions,
        CancellationToken cancellationToken
    ) =>
        (await DeclarationsAtAsync(positions, cancellationToken)).Map(declarations =>
            declarations.ToDictionary(
                declaration => declaration.Key,
                declaration => declaration.Value.Name
            )
        );

    /// <summary>
    /// Returns the innermost declaration that contains the line, or null.
    /// </summary>
    public async Task<Result<SymbolDeclaration?>> DeclarationAtAsync(
        string file,
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
        IEnumerable<(string File, LspSymbol[] Symbols)> documents,
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
        {
            return "Send one candidate symbol instead.";
        }

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
        IReadOnlyDictionary<string, HashSet<string>> projectsByFile
    ) =>
        string.Equals(declaration.Name, symbol.Name, StringComparison.Ordinal)
        && declaration.IsPrivate == symbol.IsPrivate
        && SharesProject(declaration.File, symbol.File, projectsByFile);

    // Without known projects, same-named declarations count as parts of one partial type
    private static bool SharesProject(
        string first,
        string second,
        IReadOnlyDictionary<string, HashSet<string>> projectsByFile
    ) =>
        string.Equals(first, second, StringComparison.Ordinal)
        || !projectsByFile.TryGetValue(first, out var firstProjects)
        || !projectsByFile.TryGetValue(second, out var secondProjects)
        || firstProjects.Count == 0
        || secondProjects.Count == 0
        || firstProjects.Overlaps(secondProjects);

    private async Task<Result<Dictionary<string, HashSet<string>>>> ProjectsByFileAsync(
        List<SymbolDeclaration> declarations,
        CancellationToken cancellationToken
    )
    {
        var projectsByFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sharedNames = declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .Where(group =>
                group
                    .DistinctBy(declaration => declaration.File, StringComparer.Ordinal)
                    .Skip(1)
                    .Any()
            );
        foreach (
            var file in sharedNames
                .SelectMany(group => group)
                .Select(d => d.File)
                .Distinct(StringComparer.Ordinal)
        )
        {
            if (
                (await session.OpenDocumentAsync(file, cancellationToken)).IsError(
                    out var openError,
                    out var uri
                )
            )
            {
                return openError;
            }

            var contextLookup = await session.ProjectContextsAsync(uri, cancellationToken);
            if (contextLookup.IsError(out var contextsError, out var contexts))
            {
                return contextsError;
            }

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
                parts
                    .Select(part => part.File)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
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
    private async Task<Result<List<(string File, LspSymbol[] Symbols)>>> DocumentSymbolsAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    )
    {
        var documents = new List<(string File, LspSymbol[] Symbols)>();
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
                uris.Select(uri =>
                    session.RequestAsync(
                        LspMethods.TextDocumentDocumentSymbol,
                        LspParameters.Document(uri),
                        cancellationToken
                    )
                )
            );
            if (responses.Select(ReadSymbols).All().IsError(out var error, out var symbols))
            {
                return error;
            }

            documents.AddRange(batch.Zip(symbols));
        }

        return documents;
    }

    // Namespaces only qualify their members, so they are not declarations here
    private static IEnumerable<DeclaredSymbol> Declarations(
        LspSymbol[] symbols,
        string file,
        SymbolName? container,
        string? containerDisplay
    )
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Name is not { Length: > 0 } rawName)
            {
                continue;
            }

            if (SymbolName.Parse(rawName) is not { } member)
            {
                continue;
            }

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
                kind is not LspSymbolKinds.Namespace
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

    private static bool IsExtensionBlock(SymbolName member, int? kind) =>
        member is { Segments: [var block], Parameters: not null }
        && SymbolName.BaseName(block) is "extension"
        && kind is not LspSymbolKinds.Method;

    private async Task<
        Result<Dictionary<(string File, SourcePosition Position), SymbolDeclaration>>
    > DeclarationsAtAsync(
        IEnumerable<(string File, SourcePosition Position)> positions,
        CancellationToken cancellationToken
    )
    {
        var wanted = positions.ToHashSet();
        var symbolFiles = await DocumentSymbolsAsync(
            wanted.Select(position => position.File).Distinct(StringComparer.Ordinal),
            cancellationToken
        );
        if (symbolFiles.IsError(out var error, out var documents))
        {
            return error;
        }

        var found = new Dictionary<(string File, SourcePosition Position), SymbolDeclaration>();

        foreach (var (file, symbols) in documents)
        {
            foreach (
                var declared in Declarations(symbols, file, container: null, containerDisplay: null)
            )
            {
                var declaration = declared.Declaration;
                var position = (file, new SourcePosition(declaration.Line, declaration.Character));
                if (wanted.Contains(position))
                {
                    found.TryAdd(position, declaration);
                }
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
        var response = await session.RequestAsync(
            LspMethods.WorkspaceSymbol,
            new JsonObject { ["query"] = query },
            cancellationToken
        );
        if (response.IsError(out var requestError, out var symbols))
        {
            return requestError;
        }

        if (symbols.ReadArray<LspSymbol>().IsError(out var error, out var results))
        {
            return error;
        }

        var matches = new List<WorkspaceMatch>();
        foreach (var result in results)
        {
            if (
                SymbolName.Parse(result.Name) is { } found
                && result.Location is { Uri: { } uri, Range: { } range }
                && paths.TryGetWorkspaceFile(uri) is { } file
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
    }

    private static bool IsNamed(WorkspaceMatch match, string name) =>
        string.Equals(match.Name, name, StringComparison.Ordinal);

    private static IEnumerable<string> FilesNamed(
        IEnumerable<WorkspaceMatch> matches,
        string name
    ) =>
        matches
            .Where(match => IsNamed(match, name))
            .Select(match => match.File)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    // An unreadable candidate file must not hide the error that the suggestions explain
    // A failed Roslyn request has an RPC code, so it reaches the caller
    private static bool IsUnreadable(ToolError error) =>
        error
            is { Code: ErrorCodes.Io or ErrorCodes.InvalidRequest }
                or { Code: ErrorCodes.Lsp, RpcCode: null };

    /// <summary>
    /// Returns the error of a failed Roslyn request in place of the missing symbol.
    /// </summary>
    private async Task<ToolError> NotFoundAsync(
        string text,
        SymbolName requested,
        string name,
        List<WorkspaceMatch>? matches,
        CancellationToken cancellationToken
    )
    {
        if (matches is null)
        {
            var workspaceMatches = await WorkspaceMatchesAsync(name, cancellationToken);
            if (workspaceMatches.IsError(out var matchError, out matches))
            {
                return matchError;
            }
        }

        var containerLookup = await ContainerAsync(requested, name, matches, cancellationToken);
        if (containerLookup.IsError(out var containerError, out var container))
        {
            return containerError;
        }

        var typeExists = container is { SimilarTypes: null };
        // From other types, only a member with the exact name suggests the caller named the wrong type
        var suggested = await SuggestionsAsync(
            name,
            container?.Members ?? [],
            typeExists ? [.. matches.Where(match => IsNamed(match, name))] : matches,
            cancellationToken
        );
        if (suggested.IsError(out var suggestionsError, out var suggestions))
        {
            return suggestionsError;
        }

        if (container is not null)
        {
            var display = string.Join('.', container.Name.Segments.Select(SymbolName.BaseName));
            // When only the parameters differ, the type has the member
            if (typeExists && !container.Members.Exists(member => IsNamed(member, name)))
            {
                return new ToolError(
                    ErrorCodes.SymbolNotFound,
                    suggestions.Count == 0
                        ? $"No declaration matches '{text}' because '{display}' has no member '{name}'. Use members to list its members."
                        : $"No declaration matches '{text}' because '{display}' has no member '{name}'. Send one candidate symbol instead.",
                    suggestions
                );
            }

            if (
                container.SimilarTypes is { } similar
                && suggestions.Count > 0
                && !suggestions.Exists(suggestion => IsInside(suggestion, container.Name))
            )
            {
                var types =
                    similar.Count == 0
                        ? string.Empty
                        : $" Similar types: {string.Join(", ", similar.Select(type => type.Name).Distinct(StringComparer.Ordinal))}.";
                return new ToolError(
                    ErrorCodes.SymbolNotFound,
                    $"No declaration matches '{text}' because no type matches '{display}'.{types} Use symbols to search by name."
                );
            }
        }

        return new ToolError(
            ErrorCodes.SymbolNotFound,
            suggestions.Count == 0
                ? $"No declaration matches '{text}'. Use symbols to search by name."
                : $"No declaration matches '{text}'. Send one candidate symbol instead.",
            suggestions
        );
    }

    private async Task<Result<ContainerLookup?>> ContainerAsync(
        SymbolName requested,
        string name,
        List<WorkspaceMatch> matches,
        CancellationToken cancellationToken
    )
    {
        if (requested.Segments.Length == 1)
        {
            return _noContainer;
        }

        var container = new SymbolName([.. requested.Segments[..^1]], Parameters: null);
        var containerName = SymbolName.BaseName(container.Segments[^1]);
        var candidateLookup = await WorkspaceMatchesAsync(containerName, cancellationToken);
        if (candidateLookup.IsError(out var error, out var candidates))
        {
            return error;
        }

        var symbolFiles = await DocumentSymbolsAsync(
            FilesNamed(candidates, containerName),
            cancellationToken
        );
        if (symbolFiles.IsError(out var documentsError, out var documents))
        {
            return IsUnreadable(documentsError) ? _noContainer : documentsError;
        }

        var types = FindDeclarations(documents, container).ToList();
        if (types.Count > 0)
        {
            var memberLookup = await MemberMatchesAsync(types, cancellationToken);
            if (memberLookup.IsError(out var membersError, out var members))
            {
                return IsUnreadable(membersError) ? _noContainer : membersError;
            }

            return new ContainerLookup(container, members, SimilarTypes: null);
        }

        // The container may be a namespace, which declares no members of its own
        if (matches.Exists(match => IsNamed(match, name)))
        {
            return _noContainer;
        }

        return (
            await SuggestionsAsync(
                containerName,
                [],
                [.. candidates.Where(match => LspSymbolKinds.IsType(match.Kind))],
                cancellationToken
            )
        ).Map<ContainerLookup?>(similar => new ContainerLookup(container, [], similar));
    }

    private static bool IsInside(SymbolItem suggestion, SymbolName container) =>
        SymbolName.Parse(suggestion.Name) is { } name
        && Enumerable
            .Range(1, name.Segments.Length - 1)
            .Any(length =>
                container.Selects(
                    new SymbolName([.. name.Segments[..length]], Parameters: null),
                    exact: false
                )
            );

    private async Task<Result<List<WorkspaceMatch>>> MemberMatchesAsync(
        List<SymbolDeclaration> types,
        CancellationToken cancellationToken
    )
    {
        var members = new List<WorkspaceMatch>();
        foreach (var type in types.GroupBy(type => type.Name, StringComparer.Ordinal))
        {
            if (
                (await MembersAsync([.. type], cancellationToken)).IsError(
                    out var error,
                    out var declared
                )
            )
            {
                return error;
            }

            members.AddRange(declared.Select(WorkspaceMatch.Of));
        }

        return members;
    }

    private async Task<Result<List<SymbolItem>>> SuggestionsAsync(
        string name,
        IEnumerable<WorkspaceMatch> preferred,
        List<WorkspaceMatch> matches,
        CancellationToken cancellationToken
    )
    {
        var close = Nearest(preferred, name, byWords: false).ToList();
        // Shared words help only when no member is close in spelling
        var nearest = (close.Count > 0 ? close : Nearest(preferred, name, byWords: true))
            .Concat(Nearest(matches, name, byWords: false))
            .Distinct()
            .Take(MaximumSuggestions)
            .ToList();
        var lookup = await DeclarationsAtAsync(nearest, cancellationToken);
        if (lookup.IsError(out var error, out var declarations))
        {
            return IsUnreadable(error) ? new List<SymbolItem>() : error;
        }

        return nearest
            .Select(position => declarations.GetValueOrDefault(position))
            .OfType<SymbolDeclaration>()
            .Select(declaration => declaration.ToItem(paths))
            .ToList();
    }

    private static IEnumerable<(string File, SourcePosition Position)> Nearest(
        IEnumerable<WorkspaceMatch> matches,
        string name,
        bool byWords
    ) =>
        matches
            .DistinctBy(match => match.Location)
            .Select(match => match.Resembling(name, byWords))
            .OfType<Suggestion>()
            .Order(Suggestion.Nearest)
            .Select(suggestion => suggestion.Match.Location);

    private List<SymbolItem> Candidates(IEnumerable<SymbolDeclaration> declarations) =>
        [
            .. declarations
                .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.File, StringComparer.Ordinal)
                .Take(MaximumCandidates)
                .Select(declaration => declaration.ToItem(paths)),
        ];

    private static Result<LspSymbol[]> ReadSymbols(Result<JsonNode?> response) =>
        response switch
        {
            ToolError error => error,
            JsonNode result => result.ReadArray<LspSymbol>(),
            null => Array.Empty<LspSymbol>(),
        };

    private enum NameResemblance
    {
        SameName = 0,
        ContainsName = 1,
        Typo = 2,
        SharedWords = 3,
    }

    private readonly record struct WorkspaceMatch(
        string Name,
        string File,
        SourcePosition Position,
        int? Kind
    )
    {
        public (string File, SourcePosition Position) Location => (File, Position);

        public static WorkspaceMatch Of(SymbolDeclaration declaration) =>
            new(
                SymbolName.BaseName(SymbolName.Parse(declaration.Name)!.Segments[^1]),
                declaration.File,
                new SourcePosition(declaration.Line, declaration.Character),
                declaration.Kind
            );

        /// <param name="byWords">
        /// Also accepts a name that shares most words, such as OnPlaybackStart for ReportPlaybackStart.
        /// </param>
        public Suggestion? Resembling(string requested, bool byWords)
        {
            var distance = Levenshtein.Distance(
                requested.ToLowerInvariant(),
                Name.ToLowerInvariant()
            );
            if (string.Equals(Name, requested, StringComparison.OrdinalIgnoreCase))
            {
                return new Suggestion(this, NameResemblance.SameName, distance);
            }

            if (Name.Contains(requested, StringComparison.OrdinalIgnoreCase))
            {
                return new Suggestion(this, NameResemblance.ContainsName, distance);
            }

            if (distance <= Math.Min(MaximumTypoDistance, requested.Length / 3))
            {
                return new Suggestion(this, NameResemblance.Typo, distance);
            }

            return byWords && SharesMostWords(requested, Name)
                ? new Suggestion(this, NameResemblance.SharedWords, distance)
                : null;
        }

        private static bool SharesMostWords(string requested, string name)
        {
            var words = Words(requested);
            var shared = words.Count(Words(name).Contains);
            return shared >= MinimumSharedWords && shared * 2 >= words.Count;
        }

        // PascalCase words
        private static HashSet<string> Words(string name)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var start = 0;
            for (var index = 1; index <= name.Length; index++)
            {
                if (index == name.Length || char.IsUpper(name[index]))
                {
                    words.Add(name[start..index]);
                    start = index;
                }
            }

            return words;
        }
    }

    private readonly record struct Suggestion(
        WorkspaceMatch Match,
        NameResemblance Kind,
        int Distance
    )
    {
        public static IComparer<Suggestion> Nearest { get; } = Comparer<Suggestion>.Create(Compare);

        private static int Compare(Suggestion first, Suggestion second)
        {
            var comparison = first.Kind.CompareTo(second.Kind);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = first.Distance.CompareTo(second.Distance);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(first.Match.Name, second.Match.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(first.Match.File, second.Match.File);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = first.Match.Position.Line.CompareTo(second.Match.Position.Line);
            return comparison != 0
                ? comparison
                : first.Match.Position.Character.CompareTo(second.Match.Position.Character);
        }
    }

    private sealed record ContainerLookup(
        SymbolName Name,
        List<WorkspaceMatch> Members,
        List<SymbolItem>? SimilarTypes
    );

    private readonly record struct DeclaredSymbol(
        SymbolName Name,
        bool IsConstructor,
        SymbolDeclaration Declaration
    );
}
