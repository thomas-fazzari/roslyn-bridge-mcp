// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynBridge.Extension;

/// <summary>
/// Finds callers as callHierarchy/incomingCalls does, plus the calls through base and interface members
/// and the calls that the compiler inserts without call syntax.
/// </summary>
/// <remarks>
/// The incomingCalls handler keeps only direct calls, so it misses every call made through an interface.
/// </remarks>
internal sealed class CallersHandler
    : IExtensionDocumentMessageHandler<PositionRequest, CallersResponse>
{
    private static readonly CallersResponse _notFound = new(Found: false, []);

    public async Task<CallersResponse> ExecuteAsync(
        PositionRequest message,
        ExtensionDocumentMessageContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.Solution.GetDocument(context.TextDocument.Id) is not { } document)
            return _notFound;

        if (await document.GetPositionAsync(message, cancellationToken) is not { } position)
            return _notFound;

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            position,
            cancellationToken
        );
        // callHierarchy keeps handling the other symbols, such as fields and types
        if (symbol?.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event))
            return _notFound;

        var solution = document.Project.Solution;
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken);
        // Each target framework of a project has its own tree and calling symbol for the same file
        var seen = new HashSet<SourceSite>();
        var found = new Callers();
        // Direct callers come first, so a call site listed under several members stays direct
        foreach (var caller in callers)
        {
            var locations = caller.IsDirect
                ? caller.Locations
                : await ReachingAsync(
                    caller.Locations,
                    symbol.ContainingType,
                    solution,
                    cancellationToken
                );
            found.Add(
                caller.CallingSymbol,
                caller.IsDirect ? null : caller.CalledSymbol.MemberName,
                Unseen(locations, solution, seen, cancellationToken)
            );
        }

        foreach (var call in await ImplicitCallers.FindAsync(symbol, solution, cancellationToken))
        {
            found.Add(
                call.Caller,
                through: null,
                Unseen([call.Site], solution, seen, cancellationToken)
            );
        }

        var calls = new List<IncomingCall>();
        foreach (var caller in found.InOrder)
        {
            var declared = CallItems.DeclaringSymbol(caller.Symbol);
            var sites = caller.Sites.OrderBy(site => site.SourceSpan.Start).ToList();
            if (
                await CallItems.CreateAsync(
                    declared,
                    declared,
                    solution,
                    sites[0].SourceTree,
                    cancellationToken
                )
                is not { } item
            )
            {
                continue;
            }

            calls.Add(
                new IncomingCall(item, [.. sites.Select(site => site.ToLspRange())], caller.Through)
            );
        }

        return new CallersResponse(Found: true, calls);
    }

    private static List<Location> Unseen(
        IEnumerable<Location> locations,
        Solution solution,
        HashSet<SourceSite> seen,
        CancellationToken cancellationToken
    ) =>
        [
            .. locations.Where(location =>
                location.IsVisibleSourceLocation(cancellationToken)
                && solution.GetDocument(location.SourceTree)
                    is { FilePath: { } path }
                        and not SourceGeneratedDocument
                && seen.Add(new SourceSite(path, location.SourceSpan))
            ),
        ];

    // A call through a base or interface member reaches the member only on a receiver that can hold its type
    private static async Task<List<Location>> ReachingAsync(
        IEnumerable<Location> locations,
        INamedTypeSymbol target,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var reaching = new List<Location>();
        foreach (var location in locations)
        {
            if (await MayHoldAsync(location, target, solution, cancellationToken))
                reaching.Add(location);
        }

        return reaching;
    }

    /// <returns>True unless the static type of the receiver at <paramref name="location"/> cannot hold a <paramref name="target"/>.</returns>
    private static async Task<bool> MayHoldAsync(
        Location location,
        INamedTypeSymbol target,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        if (
            location.SourceTree is not { } tree
            || solution.GetDocument(tree) is not { } document
            || await document.GetSemanticModelAsync(cancellationToken) is not { } model
        )
        {
            return true;
        }

        var root = await tree.GetRootAsync(cancellationToken);
        var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
        if (
            ReceiverType(model, node, cancellationToken)
            is not {
                TypeKind: not (TypeKind.TypeParameter or TypeKind.Dynamic or TypeKind.Error)
            } receiver
        )
        {
            return true;
        }

        // A project sees the target type through its own compilation
        return SymbolFinder
                .FindSimilarSymbols(target, model.Compilation, cancellationToken)
                .FirstOrDefault()
                is not { } held
            || model.Compilation.ClassifyCommonConversion(held, receiver).Exists;
    }

    // Roslyn also reports a local declaration of a disposable type as a call of Dispose
    private static ITypeSymbol? ReceiverType(
        SemanticModel model,
        SyntaxNode node,
        CancellationToken cancellationToken
    )
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            switch (model.GetOperation(current, cancellationToken))
            {
                case IInvocationOperation invocation:
                    return invocation.Instance?.Type;
                case IMemberReferenceOperation reference:
                    return reference.Instance?.Type;
                case IVariableDeclarationOperation declaration:
                    return declaration.GetDeclaredVariables().FirstOrDefault()?.Type;
                case IVariableDeclarationGroupOperation group:
                    return group
                        .Declarations.SelectMany(declaration => declaration.GetDeclaredVariables())
                        .FirstOrDefault()
                        ?.Type;
            }

            if (current is StatementSyntax)
                return null;
        }

        return null;
    }

    private sealed record Caller(ISymbol Symbol, string? Through, List<Location> Sites);

    private readonly record struct CallerKey(DeclarationKey? Declaration, string? Through);

    // The calls of one member, direct or through the same base or interface member
    private sealed class Callers
    {
        private readonly Dictionary<CallerKey, Caller> _byKey = [];
        private readonly List<Caller> _inOrder = [];

        public IReadOnlyList<Caller> InOrder => _inOrder;

        public void Add(ISymbol symbol, string? through, List<Location> sites)
        {
            if (sites.Count == 0)
                return;

            var key = new CallerKey(CallItems.DeclaringSymbol(symbol).DeclarationKey, through);
            if (_byKey.TryGetValue(key, out var existing))
            {
                existing.Sites.AddRange(sites);
                return;
            }

            var caller = new Caller(symbol, through, sites);
            _byKey.Add(key, caller);
            _inOrder.Add(caller);
        }
    }
}
