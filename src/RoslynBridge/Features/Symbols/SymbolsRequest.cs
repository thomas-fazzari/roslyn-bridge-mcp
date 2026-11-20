// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Symbols;

internal sealed record SymbolsRequest(string? File, string? Query, Paging Page) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy => new(LspAction.Symbols);

    public override SymbolsRequest WithFiles(FileResolver resolve) =>
        this with
        {
            File = resolve(File),
        };
}
