// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Symbols;
using RoslynBridge.Workspace;

namespace RoslynBridge.Features.Symbols;

internal sealed class UnusedHandler(
    RoslynSession session,
    WorkspacePaths paths,
    FileSelection selection,
    SymbolResolver resolver
) : IToolHandler<UnusedRequest, UnusedSymbols>
{
    /// <summary>
    /// The types and members of the selected files that no live code uses, in file and position order.
    /// </summary>
    public async Task<Result<UnusedSymbols>> HandleAsync(
        UnusedRequest request,
        CancellationToken cancellationToken
    )
    {
        var selected = selection.SelectAtMost(request.File, "Unused", cancellationToken);
        if (selected.IsError(out var selectError, out var files))
            return selectError;

        if (files.FirstOrDefault(file => !WorkspacePaths.IsExistingCSharpFile(file)) is { } missing)
        {
            return ToolError.InvalidRequest(
                $"Not a C# file of the workspace: {paths.RelativePath(missing)}"
            );
        }

        if (await session.SynchronizeNewFilesAsync(files, cancellationToken) is ToolError syncError)
            return syncError;

        var unused = await session.FindUnusedAsync(files, cancellationToken);
        if (unused.IsError(out var error, out var report))
            return error;

        var entries = report.Symbols;
        List<int> page = [.. request.Page.Slice(Enumerable.Range(0, entries.Length))];
        var naming = await NamedAsync(entries, page, cancellationToken);
        return naming.IsError(out var namesError, out var items)
            ? namesError
            : new UnusedSymbols(
                [
                    .. page.Select(index => new UnusedSymbol(
                        items[index],
                        [.. entries[index].Via.Select(user => items[user].Name)],
                        entries[index].TestsOnly
                    )),
                ],
                entries.Length,
                request.Page.NextOffset(entries.Length),
                files.Length,
                report.Skipped,
                [.. report.Unmatched.Select(paths.RelativePath)]
            );
    }

    /// <summary>
    /// Names the entries of the page and the entries that use them, by entry index.
    /// </summary>
    private async Task<Result<Dictionary<int, SymbolItem>>> NamedAsync(
        UnusedEntry[] entries,
        List<int> page,
        CancellationToken cancellationToken
    )
    {
        List<int> indexes =
        [
            .. page.Concat(page.SelectMany(index => entries[index].Via)).Distinct(),
        ];
        var named = await resolver.NameAsync(
            [.. indexes.Select(index => entries[index].Symbol.ToSymbolItem(paths))],
            cancellationToken
        );
        return named.Map(items =>
            indexes.Zip(items).ToDictionary(entry => entry.First, entry => entry.Second)
        );
    }
}
