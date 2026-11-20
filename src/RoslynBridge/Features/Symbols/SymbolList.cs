// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;
using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Features.Symbols;

/// <param name="Total">Nested symbols included.</param>
internal sealed record SymbolList(IReadOnlyList<SymbolItem> Items, int Total, int? NextOffset)
    : ToolResult
{
    /// <summary>
    /// The offset and limit apply to nested symbols in document order. A skipped symbol leaves its listed members at its level.
    /// </summary>
    public static SymbolList From(LspSymbol[] symbols, WorkspacePaths paths, Paging page)
    {
        var skipped = page.Offset;
        var remaining = page.Limit;
        var total = Count(symbols);
        return new SymbolList(
            Page(symbols, paths, ref skipped, ref remaining),
            total,
            page.NextOffset(total)
        );
    }

    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var symbol in Items)
        {
            AppendSymbolTree(text, symbol, indent: string.Empty);
        }

        AppendPageEnd(text, Total, Items.Count == 0, NextOffset, "symbols");
    }

    private static List<SymbolItem> Page(
        LspSymbol[] symbols,
        WorkspacePaths paths,
        ref int skipped,
        ref int remaining,
        string? parent = null
    )
    {
        var items = new List<SymbolItem>();
        foreach (var symbol in symbols)
        {
            if (remaining == 0)
                break;

            if (skipped > 0)
            {
                skipped--;
                if (symbol.Children is { } listed)
                    items.AddRange(Page(listed, paths, ref skipped, ref remaining, symbol.Name));

                continue;
            }

            remaining--;
            var item = symbol.ToSymbolItem(paths, parent);
            // A document symbol range covers the whole declaration, while selectionRange is its name
            if (symbol is { SelectionRange: not null, Range: { } range })
                item = item with { End = range.End.OneBased };

            if (symbol.Children is { Length: > 0 } children)
            {
                item = item with
                {
                    Children = Page(children, paths, ref skipped, ref remaining, symbol.Name),
                };
            }

            items.Add(item);
        }

        return items;
    }

    private static int Count(LspSymbol[] symbols) =>
        symbols.Sum(symbol => 1 + Count(symbol.Children ?? []));
}
