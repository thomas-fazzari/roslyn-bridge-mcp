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
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Query is not null)
        {
            return ToolError.InvalidRequest("unused takes file, not query.");
        }

        var files = SelectFiles(request.File).Take(MaximumDiagnosticFiles + 1).ToArray();
        if (files.Length > MaximumDiagnosticFiles)
        {
            return ToolError.InvalidRequest(
                $"Unused accepts at most {MaximumDiagnosticFiles} files. Use a narrower glob."
            );
        }

        if (
            files.FirstOrDefault(file => !RoslynSession.IsCSharpFile(file) || !File.Exists(file)) is
            { } missing
        )
        {
            return ToolError.InvalidRequest(
                $"Not a C# file of the workspace: {paths.RelativePath(missing)}"
            );
        }

        await session.SynchronizeNewFilesAsync(files, cancellationToken);
        var found = await session.FindUnusedAsync(files, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var report = found.Value.Read<UnusedReport>();
        var entries = report.Symbols;
        if (entries.SelectMany(entry => entry.Via).Any(user => user < 0 || user >= entries.Length))
        {
            return ToolError.InvalidLspData("Roslyn returned an unused symbol index out of range.");
        }

        List<int> page =
        [
            .. Enumerable.Range(0, entries.Length).Skip(request.Offset).Take(request.Limit),
        ];
        List<int> named = [.. page.Concat(page.SelectMany(index => entries[index].Via)).Distinct()];
        var names = await NameSymbolsAsync(
            [.. named.Select(index => entries[index].Symbol.ToSymbolItem(paths))],
            cancellationToken
        );
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
            LspRequest.NextOffset(request.Offset, request.Limit, entries.Length),
            files.Length,
            report.Skipped,
            [.. report.Unmatched.Select(paths.RelativePath)]
        );
    }
}
