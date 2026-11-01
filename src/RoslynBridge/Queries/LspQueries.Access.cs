// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    private const int ReadHighlight = 2;
    private const int WriteHighlight = 3;

    private async Task<FoundLocations> ReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (await session.FindReferencesAsync(parameters, cancellationToken) is { } references)
        {
            return references["locations"].FindLocations(paths, skipGenerated: true) with
            {
                Generated = references["generated"]!.GetValue<int>(),
            };
        }

        // definition already gives the declaration, which would read as one more usage
        parameters["context"] = new JsonObject { ["includeDeclaration"] = false };
        var result = await session.RequestAsync(
            LspMethods.TextDocumentReferences,
            parameters,
            cancellationToken
        );
        return result.FindLocations(paths, skipGenerated: true);
    }

    private async Task<Result<ToolResult>> AccessAsync(
        LspRequest request,
        FoundLocations found,
        CancellationToken cancellationToken
    )
    {
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

    /// <summary>
    /// Roslyn highlights an argument passed by reference as a write, so its text decides whether it also reads.
    /// </summary>
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
