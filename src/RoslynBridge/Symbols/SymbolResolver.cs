// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Symbols;

/// <summary>
/// Finds the declaration of a C# symbol from its name.
/// </summary>
/// <remarks>
/// Roslyn's workspace symbol search matches names loosely and reports localized containers.
/// It only selects candidate files. Their document symbols give the exact qualified names.
/// </remarks>
internal sealed class SymbolResolver(RoslynSession session, WorkspacePaths paths)
{
    internal const int MaximumCandidates = 20;

    /// <summary>
    /// Returns the declarations of the symbol named by <paramref name="text"/>,
    /// searching only <paramref name="file"/> when it is given.
    /// A partial type has one declaration per part. The first one is where navigation starts.
    /// An ambiguous name returns an error that lists the candidate symbols.
    /// </summary>
    public async Task<Result<IReadOnlyList<SymbolDeclaration>>> ResolveAsync(
        string text,
        string? file,
        CancellationToken cancellationToken
    )
    {
        if (SymbolName.Parse(text) is not { } requested)
        {
            return ToolError.InvalidRequest($"'{text}' is not a valid symbol name.");
        }

        var files = file is null
            ? await CandidateFilesAsync(requested, cancellationToken)
            : [paths.Resolve(file)];
        var declarations = FindDeclarations(
                await DocumentSymbolsAsync(files, cancellationToken),
                requested
            )
            .ToList();

        var projectsByFile = await ProjectsByFileAsync(declarations, cancellationToken);
        var symbols = DistinctSymbols(declarations, projectsByFile);
        if (symbols.Count == 1)
        {
            var symbol = symbols[0];
            return declarations
                .Where(declaration =>
                    string.Equals(declaration.Name, symbol.Name, StringComparison.Ordinal)
                    && SharesProject(symbol.File, declaration.File, projectsByFile)
                )
                .OrderBy(declaration => declaration != symbol)
                .ToList();
        }

        if (symbols.Count == 0)
        {
            return new ToolError(
                ErrorCodes.SymbolNotFound,
                $"No declaration matches '{text}'. Use symbols to search by name."
            );
        }

        var sameNames = symbols.DistinctBy(symbol => symbol.Name, StringComparer.Ordinal).Count();
        var advice =
            sameNames < symbols.Count
                ? "Send one candidate symbol with its file."
                : "Send one candidate symbol instead.";
        return new ToolError(
            ErrorCodes.AmbiguousSymbol,
            symbols.Count > MaximumCandidates
                ? $"'{text}' matches {symbols.Count} symbols. The first {MaximumCandidates} are listed. {advice}"
                : $"'{text}' matches {symbols.Count} symbols. {advice}",
            Candidates(symbols)
        );
    }

    /// <summary>
    /// Keeps one declaration per symbol. Partial declarations share a name and a project,
    /// while same-named types in different projects are separate symbols.
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
                        SharesProject(representative.File, declaration.File, projectsByFile)
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

    private async Task<Dictionary<string, HashSet<string>>> ProjectsByFileAsync(
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
            var uri = await session.OpenDocumentAsync(file, cancellationToken);
            var contexts = await session.ProjectContextsAsync(uri, cancellationToken);
            projectsByFile[file] = contexts
                .Select(context => context?["_vs_id"]?.GetValue<string>())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }

        return projectsByFile;
    }

    /// <summary>
    /// Returns the names of the declarations at one-based positions in workspace files,
    /// in the format <see cref="ResolveAsync"/> accepts. Positions without a declaration are omitted.
    /// </summary>
    public async Task<
        IReadOnlyDictionary<(string File, SourcePosition Position), string>
    > NamesAsync(
        IEnumerable<(string File, SourcePosition Position)> positions,
        CancellationToken cancellationToken
    )
    {
        var wanted = positions.ToHashSet();
        var documents = await DocumentSymbolsAsync(
            wanted.Select(position => position.File).Distinct(StringComparer.Ordinal),
            cancellationToken
        );
        var names = new Dictionary<(string File, SourcePosition Position), string>();

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
                    names.TryAdd(position, declaration.Name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Returns the innermost declaration whose lines contain <paramref name="line"/>, or null outside any.
    /// </summary>
    public async Task<SymbolDeclaration?> DeclarationAtAsync(
        string file,
        int line,
        CancellationToken cancellationToken
    )
    {
        var documents = await DocumentSymbolsAsync([file], cancellationToken);
        return Declarations(documents[0].Symbols, file, container: null, containerDisplay: null)
            .Select(declared => declared.Declaration)
            .Where(declaration => declaration.FirstLine <= line && line <= declaration.LastLine)
            .MinBy(declaration => declaration.LastLine - declaration.FirstLine);
    }

    /// <summary>
    /// Returns the members declared directly in the parts of a type, in file and line order.
    /// Members of its extension blocks count as its own.
    /// </summary>
    public async Task<IReadOnlyList<SymbolDeclaration>> MembersAsync(
        IReadOnlyList<SymbolDeclaration> parts,
        CancellationToken cancellationToken
    )
    {
        var type = SymbolName.Parse(parts[0].Name)!;
        var documents = await DocumentSymbolsAsync(
            parts
                .Select(part => part.File)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
            cancellationToken
        );
        return
        [
            .. documents
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
                .Select(declared => declared.Declaration),
        ];
    }

    /// <summary>
    /// Returns the declarations in document symbol trees that <paramref name="requested"/> selects.
    /// Exact matches win over loose ones, so every returned name selects only its own declaration.
    /// A constructor is selected only when the request includes its parameter list.
    /// </summary>
    internal static IEnumerable<SymbolDeclaration> FindDeclarations(
        IEnumerable<(string File, JsonArray Symbols)> documents,
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

    // Opening a document changes the session, so only the symbol requests run concurrently
    private async Task<List<(string File, JsonArray Symbols)>> DocumentSymbolsAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken
    )
    {
        var documents = new List<(string File, JsonArray Symbols)>();
        // Batches stay within the open documents the session keeps
        foreach (var batch in files.Chunk(RoslynSession.MaximumOpenDocuments))
        {
            var uris = await session.OpenDocumentsAsync(batch, cancellationToken);
            var symbols = await Task.WhenAll(
                uris.Select(uri =>
                    session.RequestAsync(
                        LspMethods.TextDocumentDocumentSymbol,
                        LspParameters.Document(uri),
                        cancellationToken
                    )
                )
            );
            documents.AddRange(
                batch.Zip(symbols, (file, result) => (file, result as JsonArray ?? []))
            );
        }

        return documents;
    }

    // Namespaces only qualify their members, so they are not declarations here
    private static IEnumerable<DeclaredSymbol> Declarations(
        JsonArray symbols,
        string file,
        SymbolName? container,
        string? containerDisplay
    )
    {
        foreach (var symbol in symbols.OfType<JsonObject>())
        {
            if (symbol["name"]?.GetValue<string>() is not { Length: > 0 } rawName)
            {
                continue;
            }

            if (SymbolName.Parse(rawName) is not { } member)
            {
                continue;
            }

            var kind = symbol["kind"]?.GetValue<int>();
            var children = symbol["children"] as JsonArray ?? [];
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
                && symbol["selectionRange"]?["start"] is { } start
                && symbol["range"] is { } range
            )
            {
                yield return new DeclaredSymbol(
                    name,
                    IsConstructor(member, container),
                    new SymbolDeclaration(
                        display,
                        rawName.Trim(),
                        kind,
                        file,
                        start["line"]!.GetValue<int>() + 1,
                        start["character"]!.GetValue<int>() + 1,
                        range["start"]!["line"]!.GetValue<int>() + 1,
                        range["end"]!["line"]!.GetValue<int>() + 1
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
        member is { Segments: ["extension"], Parameters: not null }
        && kind is not LspSymbolKinds.Method;

    private static bool IsConstructor(SymbolName member, SymbolName? container) =>
        container is not null
        && member.Parameters is not null
        && string.Equals(
            member.Segments[^1],
            SymbolName.BaseName(container.Segments[^1]),
            StringComparison.Ordinal
        );

    /// <summary>
    /// Returns the workspace files that may declare the symbol. Their document symbols decide.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> CandidateFilesAsync(
        SymbolName requested,
        CancellationToken cancellationToken
    )
    {
        var segments = requested.Segments.Select(SymbolName.BaseName).ToArray();
        var files = await FilesDeclaringAsync(
            string.Join('.', segments),
            segments[^1],
            cancellationToken
        );
        // Roslyn matches a qualified query against containers, and an extension block member's
        // container is the block, so retry with the member name alone
        return files.Count == 0 && segments.Length > 1
            ? await FilesDeclaringAsync(segments[^1], segments[^1], cancellationToken)
            : files;
    }

    private async Task<SortedSet<string>> FilesDeclaringAsync(
        string query,
        string name,
        CancellationToken cancellationToken
    )
    {
        var results = await session.RequestAsync(
            LspMethods.WorkspaceSymbol,
            new JsonObject { ["query"] = query },
            cancellationToken
        );
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var result in results as JsonArray ?? [])
        {
            if (
                SymbolName.Parse(result?["name"]?.GetValue<string>() ?? string.Empty) is { } found
                && string.Equals(
                    SymbolName.BaseName(found.Segments[^1]),
                    name,
                    StringComparison.Ordinal
                )
                && result?["location"]?["uri"]?.GetValue<string>() is { } uri
                && paths.TryGetWorkspaceFile(uri) is { } file
            )
            {
                files.Add(file);
            }
        }

        return files;
    }

    private List<SymbolItem> Candidates(IEnumerable<SymbolDeclaration> declarations) =>
        [
            .. declarations
                .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.File, StringComparer.Ordinal)
                .Take(MaximumCandidates)
                .Select(declaration => declaration.ToItem(paths)),
        ];

    private readonly record struct DeclaredSymbol(
        SymbolName Name,
        bool IsConstructor,
        SymbolDeclaration Declaration
    );
}
