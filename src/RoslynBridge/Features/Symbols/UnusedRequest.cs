// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Symbols;

/// <param name="File">A path or a glob. Omitted, it selects every C# file.</param>
internal sealed record UnusedRequest(string? File, Paging Page) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy => new(LspAction.Symbols, Files: FileScope.CSharpOrGlob);

    public static Result<UnusedRequest> From(string? file, string? query, Paging page) =>
        query is null
            ? new UnusedRequest(file, page)
            : ToolError.InvalidRequest("unused takes file, not query.");

    public override UnusedRequest WithFiles(FileResolver resolve) =>
        this with
        {
            File = resolve(File),
        };
}
