// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;

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
            found.Add(
                caller.CallingSymbol,
                caller.IsDirect ? null : caller.CalledSymbol.MemberName,
                Unseen(caller.Locations, solution, seen, cancellationToken)
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
