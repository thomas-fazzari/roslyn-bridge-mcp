// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynBridge.Extension;

/// <summary>
/// Finds the types and members declared in some files that no live code uses.
/// </summary>
/// <remarks>
/// Code is live when something outside the candidates uses it, directly or through other live candidates.
/// A use from tests or from a candidate's own declaration does not count, and neither does a documentation comment.
/// </remarks>
internal sealed class UnusedHandler
    : IExtensionWorkspaceMessageHandler<UnusedRequest, UnusedResponse>
{
    private const int MaximumConcurrentSearches = 4;

    public async Task<UnusedResponse> ExecuteAsync(
        UnusedRequest message,
        ExtensionWorkspaceMessageContext context,
        CancellationToken cancellationToken
    )
    {
        var solution = context.Solution;
        var files = message.Files.ToHashSet(StringComparer.Ordinal);
        var scan = await ScanAsync(solution, files, cancellationToken);
        var candidates = scan.Candidates;
        var indexes = candidates
            .Select((candidate, index) => (candidate.Key, index))
            .ToDictionary(entry => entry.Key, entry => entry.index);
        var containers = candidates.Select(candidate => Containers(candidate, indexes)).ToArray();
        var testProjects = solution
            .Projects.Where(project => project.IsTestProject)
            .Select(project => project.Id)
            .ToHashSet();

        var uses = new IReadOnlyList<Use>[candidates.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumConcurrentSearches,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
                uses[index] = await UsesAsync(
                    new Search(index, candidates[index], indexes, files, testProjects),
                    solution,
                    token
                )
        );

        var dead = Dead(uses, containers, cancellationToken);
        return new UnusedResponse(
            Report(candidates, containers, uses, dead, files, cancellationToken),
            scan.Skipped,
            [.. files.Except(scan.Matched, StringComparer.Ordinal).Order(StringComparer.Ordinal)]
        );
    }

    private static async Task<Scan> ScanAsync(
        Solution solution,
        HashSet<string> files,
        CancellationToken cancellationToken
    )
    {
        var sources = solution
            .Projects.SelectMany(project => project.Documents)
            .Select(document => document.FilePath)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var found = new List<Candidate>();
        var seen = new HashSet<DeclarationKey>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        // A framework reaches an attributed member or an entry point through its types
        var reached = new HashSet<DeclarationKey>();
        foreach (var project in solution.Projects)
        {
            var documents = project
                .Documents.Where(document => document.FilePath is { } path && files.Contains(path))
                .ToList();
            matched.UnionWith(documents.Select(document => document.FilePath!));
            if (
                documents.Count == 0
                || await project.GetCompilationAsync(cancellationToken) is not { } compilation
            )
            {
                continue;
            }

            var entryPoint = compilation.GetEntryPoint(cancellationToken);
            foreach (var document in documents.Where(document => !document.HasGeneratedName))
            {
                var model = await document.GetSemanticModelAsync(cancellationToken);
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (model is null || root?.HasGeneratedHeader is not false)
                {
                    continue;
                }

                var declarations = root.DescendantNodes(node => node.HoldsDeclarations)
                    .Where(node => node.IsDeclaration);
                foreach (var node in declarations)
                {
                    if (
                        model.GetDeclaredSymbol(node, cancellationToken)?.Definition
                            is not { } symbol
                        || symbol.DeclarationKey is not { } key
                        || !seen.Add(key)
                    )
                    {
                        continue;
                    }

                    var isEntryPoint = SymbolEqualityComparer.Default.Equals(symbol, entryPoint);
                    if (isEntryPoint || symbol.HasFrameworkAttribute)
                    {
                        reached.UnionWith(symbol.ContainingTypeKeys);
                    }

                    if (
                        isEntryPoint
                        || !symbol.CanBeUnused
                        || !IsDeclaredIn(symbol, files, sources)
                    )
                    {
                        continue;
                    }

                    if (symbol.IsReachedByFramework)
                    {
                        skipped++;
                    }
                    else
                    {
                        found.Add(new Candidate(symbol, key, project.IsTestProject));
                    }
                }
            }
        }

        List<Candidate> kept = [.. found.Where(candidate => !reached.Contains(candidate.Key))];
        return new Scan(kept, skipped + found.Count - kept.Count, matched);
    }

    // A partial type whose other parts are not requested may be used through them
    private static bool IsDeclaredIn(
        ISymbol symbol,
        HashSet<string> files,
        HashSet<string> sources
    ) =>
        symbol.DeclaringSyntaxReferences.All(reference =>
            files.Contains(reference.SyntaxTree.FilePath)
            || !sources.Contains(reference.SyntaxTree.FilePath)
        );

    private static async Task<IReadOnlyList<Use>> UsesAsync(
        Search search,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await SearchUsesAsync(search, solution, cancellationToken);
        }
        // Deleting live code costs more than keeping dead code, so a failed search keeps the candidate
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new Use([], Test: false)];
        }
    }

    private static async Task<IReadOnlyList<Use>> SearchUsesAsync(
        Search search,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var uses = new List<Use>();
        var references = await SymbolFinder.FindReferencesAsync(
            search.Candidate.Symbol,
            solution,
            cancellationToken
        );
        foreach (var reference in references.SelectMany(referenced => referenced.Locations))
        {
            var document = reference.Document;
            if (
                !reference.Location.IsInSource
                || await document.GetSyntaxRootAsync(cancellationToken) is not { } root
            )
            {
                continue;
            }

            var token = root.FindToken(reference.Location.SourceSpan.Start, findInsideTrivia: true);
            if (
                token.Parent?.FirstAncestorOrSelf<SyntaxNode>(node =>
                    node is DocumentationCommentTriviaSyntax or UsingDirectiveSyntax
                )
                is not null
            )
            {
                continue;
            }

            // Owners are declared in the requested files, so a use elsewhere needs no semantic model
            var owners =
                document.FilePath is { } path
                && search.Files.Contains(path)
                && await document.GetSemanticModelAsync(cancellationToken) is { } model
                    ? Owners(token, model, search.Indexes, cancellationToken)
                    : [];
            if (!owners.Contains(search.Index))
            {
                var fromTests =
                    !search.Candidate.IsTest && search.TestProjects.Contains(document.Project.Id);
                uses.Add(new Use(owners, fromTests));
            }
        }

        return uses;
    }

    // The greatest fixed point, so code that only uses itself stays dead
    private static bool[] Dead(
        IReadOnlyList<Use>[] uses,
        IReadOnlyList<int>[] containers,
        CancellationToken cancellationToken
    )
    {
        var dead = Enumerable.Repeat(true, uses.Length).ToArray();
        var changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (var index = 0; index < uses.Length; index++)
            {
                if (!dead[index] || !uses[index].Any(use => !use.Test && IsLive(use, dead)))
                {
                    continue;
                }

                // A used member keeps its types, as an extension method keeps its static class
                dead[index] = false;
                foreach (var container in containers[index])
                {
                    dead[container] = false;
                }

                changed = true;
            }
        }

        return dead;
    }

    // A dead member of a dead type is reported with its type, which takes its uses
    private static List<UnusedSymbol> Report(
        List<Candidate> candidates,
        IReadOnlyList<int>[] containers,
        IReadOnlyList<Use>[] uses,
        bool[] dead,
        HashSet<string> files,
        CancellationToken cancellationToken
    )
    {
        var found = new List<(int Index, CallItem Item)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (
                dead[index]
                && !containers[index].Any(container => dead[container])
                && candidates[index].Symbol.ToDeclarationItem(files, cancellationToken) is { } item
            )
            {
                found.Add((index, item));
            }
        }

        var reported = found
            .OrderBy(entry => entry.Item.Uri, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item.SelectionRange.Start.Line)
            .ThenBy(entry => entry.Item.SelectionRange.Start.Character)
            .ToList();
        var positions = reported
            .Select((entry, position) => (entry.Index, position))
            .ToDictionary(entry => entry.Index, entry => entry.position);
        var via = reported.Select(_ => new SortedSet<int>()).ToArray();
        var testsOnly = new bool[reported.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!dead[index] || PositionOf(index) is not { } target)
            {
                continue;
            }

            testsOnly[target] |= uses[index].Any(use => use.Test && IsLive(use, dead));
            var users = uses[index]
                .Where(use => !use.Test)
                .SelectMany(use => use.Owners.Where(owner => dead[owner]).Take(1))
                .Select(PositionOf);
            foreach (var user in users)
            {
                if (user is { } position && position != target)
                {
                    via[target].Add(position);
                }
            }
        }

        return
        [
            .. reported.Select(
                (entry, position) =>
                    new UnusedSymbol(entry.Item, [.. via[position]], testsOnly[position])
            ),
        ];

        // A member of a reported type maps to that type, its outermost dead container
        int? PositionOf(int index)
        {
            if (positions.TryGetValue(index, out var position))
            {
                return position;
            }

            foreach (var container in containers[index])
            {
                if (positions.TryGetValue(container, out var outer))
                {
                    return outer;
                }
            }

            return null;
        }
    }

    private static bool IsLive(Use use, bool[] dead) => use.Owners.All(owner => !dead[owner]);

    /// <summary>
    /// The candidates whose declaration holds the reference, innermost first.
    /// </summary>
    private static List<int> Owners(
        SyntaxToken token,
        SemanticModel model,
        Dictionary<DeclarationKey, int> indexes,
        CancellationToken cancellationToken
    )
    {
        var owners = new List<int>();
        var declaration = token
            .Parent?.AncestorsAndSelf()
            .FirstOrDefault(node => node.IsDeclaration);
        for (
            var symbol = declaration is null
                ? null
                : model.GetDeclaredSymbol(declaration, cancellationToken);
            symbol is not null;
            symbol = symbol.ContainingSymbol
        )
        {
            if (
                symbol.Definition.DeclarationKey is { } key
                && indexes.TryGetValue(key, out var owner)
            )
            {
                owners.Add(owner);
            }
        }

        return owners;
    }

    private static List<int> Containers(
        Candidate candidate,
        Dictionary<DeclarationKey, int> indexes
    ) =>
        [
            .. candidate
                .Symbol.ContainingTypeKeys.Where(indexes.ContainsKey)
                .Select(key => indexes[key]),
        ];

    /// <param name="IsTest">True when a test project declares the candidate, so test uses count.</param>
    private sealed record Candidate(ISymbol Symbol, DeclarationKey Key, bool IsTest);

    /// <param name="Skipped">Candidates left out because a framework may reach them by reflection.</param>
    /// <param name="Matched">Requested files that a project compiles.</param>
    private sealed record Scan(List<Candidate> Candidates, int Skipped, HashSet<string> Matched);

    private sealed record Search(
        int Index,
        Candidate Candidate,
        Dictionary<DeclarationKey, int> Indexes,
        HashSet<string> Files,
        HashSet<ProjectId> TestProjects
    );

    /// <param name="Owners">The candidates whose declaration holds the use, innermost first.</param>
    /// <param name="Test">True when a test project holds the use of a candidate outside the tests.</param>
    private sealed record Use(IReadOnlyList<int> Owners, bool Test);
}
