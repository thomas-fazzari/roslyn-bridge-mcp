// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp;

namespace RoslynBridge.Features.Symbols;

internal sealed class SymbolsHandler(RoslynSession session, WorkspacePaths paths)
    : IToolHandler<SymbolsRequest, SymbolList>
{
    public async Task<Result<SymbolList>> HandleAsync(
        SymbolsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.File is null)
        {
            var query = request.Query ?? string.Empty;
            var response = await session.WorkspaceSymbolsAsync(query, cancellationToken);
            // Roslyn returns fuzzy matches in file order, so exact and prefix matches come first
            return response
                .Map(found =>
                    found
                        .OrderBy(symbol => MatchRank(symbol.Name, query))
                        .ThenBy(symbol => symbol.Location?.Uri, StringComparer.Ordinal)
                        .ThenBy(symbol => symbol.Location?.Range?.Start)
                        .ToArray()
                )
                .Map(found => SymbolList.From(found, paths, request.Page));
        }

        if (paths.Resolve(request.File).IsError(out var pathError, out var file))
            return pathError;

        var opened = await session.OpenDocumentAsync(file, cancellationToken);
        return opened.IsError(out var openError, out var uri)
            ? openError
            : (await session.DocumentSymbolsAsync(uri, cancellationToken)).Map(symbols =>
                SymbolList.From(symbols, paths, request.Page)
            );
    }

    private static int MatchRank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.Ordinal))
            return 0;

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            return 1;

        return name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }
}
