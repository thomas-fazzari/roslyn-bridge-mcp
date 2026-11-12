// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynBridge.Extension;

/// <summary>
/// Finds callers as callHierarchy/incomingCalls does, plus the calls through base and interface members.
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
        {
            return _notFound;
        }

        if (await document.GetPositionAsync(message, cancellationToken) is not { } position)
        {
            return _notFound;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            position,
            cancellationToken
        );
        // callHierarchy keeps handling the other symbols, such as fields and types
        if (symbol?.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event))
        {
            return _notFound;
        }

        var solution = document.Project.Solution;
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken);
        var seen = new HashSet<(SyntaxTree, TextSpan)>();
        var calls = new List<IncomingCall>();
        // Direct callers come first, so a call site listed under several members stays direct
        foreach (var caller in callers)
        {
            var sites = caller
                .Locations.Where(location =>
                    location.IsVisibleSourceLocation(cancellationToken)
                    && solution.GetDocument(location.SourceTree) is { FilePath: not null } site
                    && site is not SourceGeneratedDocument
                    && seen.Add((location.SourceTree!, location.SourceSpan))
                )
                .ToList();
            if (
                sites.Count == 0
                || await ItemAsync(caller.CallingSymbol, solution, sites[0], cancellationToken)
                    is not { } item
            )
            {
                continue;
            }

            calls.Add(
                new IncomingCall(
                    item,
                    [.. sites.Select(site => site.ToLspRange())],
                    caller.IsDirect ? null : caller.CalledSymbol.MemberName
                )
            );
        }

        return new CallersResponse(Found: true, calls);
    }

    private static async Task<CallItem?> ItemAsync(
        ISymbol caller,
        Solution solution,
        Location site,
        CancellationToken cancellationToken
    )
    {
        // An implicit constructor has no declaration, so its type stands in for it
        var declared = caller is IMethodSymbol { DeclaringSyntaxReferences.IsEmpty: true } method
            ? method.ContainingType
            : caller;
        var references = declared.DeclaringSyntaxReferences;
        var reference = references.FirstOrDefault(syntax => syntax.SyntaxTree == site.SourceTree)
            ?? references.FirstOrDefault();
        if (
            reference is null
            || solution.GetDocument(reference.SyntaxTree)?.FilePath is not { } path
        )
        {
            return null;
        }

        var declaration = await reference.GetSyntaxAsync(cancellationToken);
        var name = declared.Locations.FirstOrDefault(location =>
            location.SourceTree == reference.SyntaxTree
            && declaration.Span.Contains(location.SourceSpan)
        );
        return new CallItem(
            declared.MemberName,
            declared.LspKind,
            path.ToLspUri(),
            declaration.GetLocation().ToLspRange(),
            (name ?? declaration.GetLocation()).ToLspRange()
        );
    }
}
