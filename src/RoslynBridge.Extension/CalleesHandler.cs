// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynBridge.Extension;

/// <summary>
/// Finds callees as callHierarchy/outgoingCalls does, plus the calls that the compiler inserts without call syntax.
/// </summary>
/// <remarks>
/// <para>Like that handler, it lists the members declared in source, with their call sites in the requested declaration.</para>
/// <para>A property, field or event is listed as well, so that the caller can count the accesses.</para>
/// </remarks>
internal sealed class CalleesHandler
    : IExtensionDocumentMessageHandler<PositionRequest, CalleesResponse>
{
    private static readonly CalleesResponse _notFound = new(Found: false, []);

    public async Task<CalleesResponse> ExecuteAsync(
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
        // callHierarchy keeps handling the other symbols, such as types
        if (symbol?.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event))
            return _notFound;

        var solution = document.Project.Solution;
        var walker = new CallWalker(CallScope.AllCalls);
        var walked = false;
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(cancellationToken);
            if (
                !HasBody(declaration)
                || solution.GetDocument(reference.SyntaxTree) is not { } source
                || await source.GetSemanticModelAsync(cancellationToken) is not { } model
            )
            {
                continue;
            }

            walked = true;
            foreach (var operation in OperationRoots(model, declaration, cancellationToken))
            {
                walker.Visit(operation);
            }
        }

        if (!walked)
            return _notFound;

        return await CallsAsync(walker.Calls, solution, cancellationToken) is { } calls
            ? new CalleesResponse(Found: true, calls)
            : _notFound;
    }

    private static async Task<List<OutgoingCall>?> CallsAsync(
        IReadOnlyList<CallSite> sites,
        Solution solution,
        CancellationToken cancellationToken
    )
    {
        var calls = new List<OutgoingCall>();
        var callees = sites
            .Where(site => IsDeclaredInSource(site.Callee))
            .GroupBy(site => site.Callee, SymbolEqualityComparer.Default);
        foreach (var callee in callees)
        {
            var declared = CallItems.DeclaringSymbol(callee.Key!);
            // The URI of a generated document needs its generator's identity, which Roslyn keeps internal
            if (CallItems.IsOnlyGenerated(declared, solution))
                return null;

            if (
                await CallItems.CreateAsync(
                    callee.Key!,
                    declared,
                    solution,
                    near: null,
                    cancellationToken
                )
                is not { } item
            )
            {
                continue;
            }

            calls.Add(
                new OutgoingCall(
                    item,
                    [
                        .. callee
                            .DistinctBy(site => site.Site.SourceSpan)
                            .Select(site => site.Site.ToLspRange()),
                    ]
                )
            );
        }

        return calls;
    }

    // Declarations without a body, such as a type of a primary ctor, stay with callHierarchy
    private static bool HasBody(SyntaxNode declaration) =>
        declaration
            is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or BasePropertyDeclarationSyntax;

    // The roots that outgoingCalls walks: the body, and also the attributes, parameter defaults and initializers
    private static IEnumerable<IOperation> OperationRoots(
        SemanticModel model,
        SyntaxNode declaration,
        CancellationToken cancellationToken
    )
    {
        var seen = new HashSet<SyntaxNode>();
        foreach (var node in declaration.DescendantNodesAndSelf())
        {
            if (model.GetOperation(node, cancellationToken) is not { } operation)
                continue;

            while (operation.Parent is { } parent)
            {
                operation = parent;
            }

            if (seen.Add(operation.Syntax))
                yield return operation;
        }
    }

    // An anonymous type declares its properties in source, but nothing names them
    private static bool IsDeclaredInSource(ISymbol callee) =>
        callee.ContainingType is not { IsAnonymousType: true }
        && !CallItems.DeclaringSymbol(callee).DeclaringSyntaxReferences.IsEmpty;
}
