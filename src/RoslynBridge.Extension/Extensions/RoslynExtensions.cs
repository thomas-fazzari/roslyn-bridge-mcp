// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynBridge.Extension;

// Copies of internal Roslyn helpers, so results match textDocument/references
internal static class RoslynExtensions
{
    extension(SyntaxTree tree)
    {
        public bool IsHiddenPosition(int position, CancellationToken cancellationToken) =>
            tree.HasHiddenRegions()
            && tree.GetLineVisibility(position, cancellationToken)
                is LineVisibility.Hidden
                    or LineVisibility.BeforeFirstLineDirective;
    }

    extension(Location location)
    {
        /// <summary>
        /// False on lines hidden by <c>#line hidden</c>, which Roslyn leaves out of references.
        /// </summary>
        public bool IsVisibleSourceLocation(CancellationToken cancellationToken) =>
            location.IsInSource
            && location.SourceTree is { } tree
            && !tree.IsHiddenPosition(location.SourceSpan.Start, cancellationToken);
    }

    extension(SyntaxToken token)
    {
        public bool IsLiteralTouching(int position) =>
            token.Span.Start <= position
            && position <= token.Span.End
            && token.Parent is LiteralExpressionSyntax;
    }
}
