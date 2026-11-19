// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    /// <summary>
    /// The types and members of the selected files that no live code uses, in file and position order.
    /// </summary>
    public async Task<Result<ToolResult>> UnusedAsync(
        SymbolsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Query is not null)
        {
            return ToolError.InvalidRequest("unused takes file, not query.");
        }

        if (
            SelectFiles(request.File, cancellationToken)
                .IsError(out var selectError, out var selected)
        )
        {
            return selectError;
        }

        var files = selected.Take(MaximumDiagnosticFiles + 1).ToArray();
        if (files.Length > MaximumDiagnosticFiles)
        {
            return ToolError.InvalidRequest(
                $"Unused accepts at most {MaximumDiagnosticFiles} files. Use a narrower glob."
            );
        }

        if (
            files.FirstOrDefault(file =>
                !WorkspacePaths.IsCSharpFile(file) || !File.Exists(file)
            ) is
            { } missing
        )
        {
            return ToolError.InvalidRequest(
                $"Not a C# file of the workspace: {paths.RelativePath(missing)}"
            );
        }

        if (await session.SynchronizeNewFilesAsync(files, cancellationToken) is ToolError syncError)
        {
            return syncError;
        }

        var unused = await session.FindUnusedAsync(files, cancellationToken);
        if (unused.IsError(out var error, out var found))
        {
            return error;
        }

        if (found.Read<UnusedReport>().IsError(out var invalid, out var report))
        {
            return invalid;
        }

        var entries = report.Symbols;
        if (entries.SelectMany(entry => entry.Via).Any(user => user < 0 || user >= entries.Length))
        {
            return ToolError.InvalidLspData("Roslyn returned an unused symbol index out of range.");
        }

        List<int> page =
        [
            .. Enumerable
                .Range(0, entries.Length)
                .Skip(request.Page.Offset)
                .Take(request.Page.Limit),
        ];
        List<int> named = [.. page.Concat(page.SelectMany(index => entries[index].Via)).Distinct()];
        var naming = await NameSymbolsAsync(
            [.. named.Select(index => entries[index].Symbol.ToSymbolItem(paths))],
            cancellationToken
        );
        if (naming.IsError(out var namesError, out var names))
        {
            return namesError;
        }

        var item = named.Zip(names).ToDictionary(entry => entry.First, entry => entry.Second);

        return new UnusedSymbols(
            [
                .. page.Select(index => new UnusedSymbol(
                    item[index],
                    [.. entries[index].Via.Select(user => item[user].Name)],
                    entries[index].TestsOnly
                )),
            ],
            entries.Length,
            Paging.NextOffset(request.Page.Offset, request.Page.Limit, entries.Length),
            files.Length,
            report.Skipped,
            [.. report.Unmatched.Select(paths.RelativePath)]
        );
    }
}
