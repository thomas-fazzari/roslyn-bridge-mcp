// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;

namespace RoslynBridge.Extension;

/// <param name="Caller">The member that holds the call, as the callers finder of Roslyn names it.</param>
/// <param name="Site">The token that the call is reported at.</param>
internal readonly record struct ImplicitCaller(ISymbol Caller, Location Site);

/// <summary>
/// Finds the calls of a member that the compiler inserts without call syntax, which Roslyn's finders miss.
/// </summary>
/// <remarks>
/// <para>A scan binds only the syntax that can hide such a call, in the projects that can see the member.</para>
/// <para>A call of an implicit user-defined conversion is not found, since that needs the binding of all code.</para>
/// </remarks>
internal static class ImplicitCallers
{
    public static async Task<IReadOnlyList<ImplicitCaller>> FindAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var target = CallWalker.Normalize(symbol);
        var scans = ImplicitScans.For(target);
        if (scans == ImplicitScan.None)
            return [];

        var callers = new List<ImplicitCaller>();
        foreach (var project in ProjectsThatSee(target, solution, cancellationToken))
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ScanAsync(document, scans, target, callers, cancellationToken);
            }
        }

        return callers;
    }

    private static async Task ScanAsync(
        Document document,
        ImplicitScan scans,
        ISymbol target,
        List<ImplicitCaller> callers,
        CancellationToken cancellationToken
    )
    {
        if (!scans.MayOccurIn(await document.GetTextAsync(cancellationToken)))
            return;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
            return;

        // A candidate inside a walked one is walked with it
        SemanticModel? model = null;
        var walked = 0;
        foreach (var node in root.DescendantNodes().Where(node => scans.IsCandidate(node)))
        {
            if (node.SpanStart < walked)
                continue;

            model ??= await document.GetSemanticModelAsync(cancellationToken);
            if (model is null)
                return;

            if (OperationOf(model, node, cancellationToken) is not { } operation)
                continue;

            walked = node.Span.End;
            var walker = new CallWalker(CallScope.MissedByFindCallers);
            walker.Visit(operation);
            foreach (var call in walker.Calls.Where(call => IsSameMember(call.Callee, target)))
            {
                var caller = EnclosingMember(model, call.Site.SourceSpan.Start, cancellationToken);
                if (caller is not null)
                    callers.Add(new ImplicitCaller(caller, call.Site));
            }
        }
    }

    // A project that is not the member's own can reach it only through a reference, or not at all for a metadata member
    private static IEnumerable<Project> ProjectsThatSee(
        ISymbol target,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var declaring = solution.GetProject(target.ContainingAssembly, cancellationToken);
        if (declaring is null)
            return solution.Projects.Where(IsCSharp);

        // Each target framework of a project is a project of its own, with its dependents
        var variants = declaring.FilePath is { } path
            ? solution.Projects.Where(project =>
                string.Equals(project.FilePath, path, StringComparison.Ordinal)
            )
            : [declaring];
        var graph = solution.GetProjectDependencyGraph();
        var ids = new HashSet<ProjectId>();
        foreach (var variant in variants)
        {
            ids.Add(variant.Id);
            ids.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(variant.Id));
        }

        return ids.Select(id => solution.GetProject(id)).OfType<Project>().Where(IsCSharp);
    }

    private static bool IsCSharp(Project project) =>
        string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal);

    // The implicit operations of a node, such as the handler creation of an interpolated string, wrap its explicit one
    private static IOperation? OperationOf(
        SemanticModel model,
        SyntaxNode node,
        CancellationToken cancellationToken
    )
    {
        var operation = model.GetOperation(node, cancellationToken);
        while (operation?.Parent is { } parent && parent.Syntax == operation.Syntax)
        {
            operation = parent;
        }

        return operation;
    }

    // Symbols of different compilations are never equal, so they are matched by their documentation ID
    // That also matches a type that an assembly forwards, such as one of netstandard and System.Collections
    private static bool IsSameMember(ISymbol callee, ISymbol target) =>
        SymbolEqualityComparer.Default.Equals(callee, target)
        || (
            callee.GetDocumentationCommentId() is { } id
            && string.Equals(id, target.GetDocumentationCommentId(), StringComparison.Ordinal)
        );

    // The member that the callers finder names: lambdas belong to their member, and accessors to their property
    private static ISymbol? EnclosingMember(
        SemanticModel model,
        int position,
        CancellationToken cancellationToken
    )
    {
        for (
            var symbol = model.GetEnclosingSymbol(position, cancellationToken);
            symbol is not null;
            symbol = symbol.ContainingSymbol
        )
        {
            switch (symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }:
                    continue;
                case IMethodSymbol { AssociatedSymbol: { } owner }:
                    return owner;
                case IMethodSymbol or IPropertySymbol or IFieldSymbol:
                    return symbol;
            }
        }

        return null;
    }
}
