// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;

namespace RoslynBridge.Extension;

internal static class CallItems
{
    /// <summary>
    /// The symbol whose declaration holds <paramref name="symbol"/>. An implicit constructor has none, so its type stands in.
    /// </summary>
    public static ISymbol DeclaringSymbol(ISymbol symbol) =>
        symbol is IMethodSymbol { DeclaringSyntaxReferences.IsEmpty: true } method
            ? method.ContainingType
            : symbol;

    /// <summary>
    /// Whether only source-generated documents declare the symbol, so that no item can name it.
    /// </summary>
    public static bool IsOnlyGenerated(ISymbol declared, Solution solution) =>
        declared.DeclaringSyntaxReferences.All(reference =>
            solution.GetDocument(reference.SyntaxTree) is SourceGeneratedDocument
        );

    /// <param name="declared">See <see cref="DeclaringSymbol"/>.</param>
    /// <param name="near">A part of a partial symbol in this tree is preferred.</param>
    /// <returns>Null when the declaration is not in a document with a path.</returns>
    public static async Task<CallItem?> CreateAsync(
        ISymbol shown,
        ISymbol declared,
        Solution solution,
        SyntaxTree? near,
        CancellationToken cancellationToken
    )
    {
        var references = declared.DeclaringSyntaxReferences;
        var reference = references.FirstOrDefault(syntax => syntax.SyntaxTree == near)
            ?? references.FirstOrDefault(syntax =>
                solution.GetDocument(syntax.SyntaxTree) is not (null or SourceGeneratedDocument)
            );
        if (
            reference is null
            || solution.GetDocument(reference.SyntaxTree) is not { FilePath: { } path }
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
            shown.MemberName,
            shown.LspKind,
            path.ToLspUri(),
            declaration.GetLocation().ToLspRange(),
            (name ?? declaration.GetLocation()).ToLspRange()
        );
    }
}
