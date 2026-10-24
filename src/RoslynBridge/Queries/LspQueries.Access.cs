// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    private const int ReadHighlight = 2;
    private const int WriteHighlight = 3;

    /// <summary>
    /// Keeps the references that Roslyn highlights as reads or as writes in their file, then pages them.
    /// </summary>
    /// <returns>The reads or writes, or the error of a failed highlight request.</returns>
    private async Task<Result<ToolResult>> AccessAsync(
        LspRequest request,
        JsonNode? references,
        CancellationToken cancellationToken
    )
    {
        var found = references.FindLocations(paths, skipGenerated: true);
        var highlighted = await HighlightedAsync(
            found.Positions,
            request.Action is LspAction.Writes,
            cancellationToken
        );
        if (highlighted.Error is { } error)
        {
            return error;
        }

        var matching = found with
        {
            Positions = [.. found.Positions.Where(highlighted.Value.Contains)],
        };
        var locations = matching.Page(request.Limit, request.Offset) with
        {
            Access = request.Action,
        };
        return request.Context ? SourceLines.Add(locations, paths) : locations;
    }

    /// <summary>
    /// Asks for the highlights of each workspace file once, at its first reference.
    /// </summary>
    /// <returns>The positions of the reads or of the writes, or the first error.</returns>
    private async Task<Result<HashSet<(string File, SourcePosition Position)>>> HighlightedAsync(
        IReadOnlyList<(string File, SourcePosition Position)> references,
        bool writes,
        CancellationToken cancellationToken
    )
    {
        var firsts = references
            .Where(reference => paths.ResultFilePath(reference.File) is not null)
            .GroupBy(reference => reference.File, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var results = await RunConcurrentlyAsync(
            firsts.Count,
            (index, token) =>
                HighlightsAsync(firsts[index].File, firsts[index].Position, writes, token),
            cancellationToken
        );
        foreach (var result in results)
        {
            if (result.Error is { } error)
            {
                return error;
            }
        }

        return results.SelectMany(result => result.Value).ToHashSet();
    }

    /// <returns>
    /// The reads or the writes in the file, with the file name of each. Roslyn highlights an argument
    /// passed by reference as a write, so its text decides whether it also reads.
    /// </returns>
    private async Task<Result<List<(string File, SourcePosition Position)>>> HighlightsAsync(
        string file,
        SourcePosition position,
        bool writes,
        CancellationToken cancellationToken
    )
    {
        var highlights = await session.RequestAsync(
            LspMethods.TextDocumentDocumentHighlight,
            await PositionParametersAsync(file, position, cancellationToken),
            cancellationToken
        );
        var written = highlights.ToHighlightStarts(WriteHighlight);
        var lines = SourceLines.TryReadLines(paths.ResultFilePath(file));
        var starts = writes
            ? written.Where(start => ByRefAccess.Of(lines, start) is not ByRefUse.Read)
            : highlights
                .ToHighlightStarts(ReadHighlight)
                .Concat(
                    written.Where(start =>
                        ByRefAccess.Of(lines, start) is ByRefUse.Read or ByRefUse.ReadWrite
                    )
                );
        return starts.Select(start => (file, start)).ToList();
    }
}
