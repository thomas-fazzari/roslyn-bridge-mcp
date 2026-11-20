// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.Navigate;

internal sealed partial class NavigateHandler
{
    /// <summary>
    /// Only the Roslyn extension answers, so a failure is an error and not an empty result.
    /// </summary>
    private async Task<Result<ToolResult>> ExpressionAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var described = await session.DescribeExpressionAsync(position, cancellationToken);
        if (described.IsError(out var error, out var facts))
            return error;

        if (!facts.Found)
            return new NoExpression();

        // The expression binds to a symbol, or else it has candidates
        var bound = facts.Symbol is not null;
        var symbols = facts.Symbol is { } symbol ? [symbol] : facts.Candidates ?? [];
        var naming = await resolver.NameAsync(
            [.. symbols.Select(item => item.ToSymbolItem(paths))],
            cancellationToken
        );
        if (naming.IsError(out var namesError, out var items))
            return namesError;

        return new ExpressionFacts(
            facts.Text,
            facts.Kind,
            facts.Declaration,
            facts.Type,
            facts.ConvertedType,
            facts.Nullability,
            facts.Conversion,
            facts.Constant,
            bound ? items[0] : null,
            facts.Instantiation,
            facts.UnboundReason,
            bound ? [] : items,
            facts.Documentation
        );
    }
}
