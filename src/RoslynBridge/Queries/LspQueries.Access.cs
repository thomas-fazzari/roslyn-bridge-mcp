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

    private async Task<FoundReferences> ReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (await session.FindReferencesAsync(parameters, cancellationToken) is { } references)
        {
            var locations = references["locations"].FindLocations(paths, skipGenerated: true) with
            {
                Generated = references["generated"]!.GetValue<int>(),
            };
            var written = references["written"].FindLocations(paths, skipGenerated: true);
            return new FoundReferences(locations, [.. written.Positions]);
        }

        // definition already gives the declaration, which would read as one more usage
        parameters["context"] = new JsonObject { ["includeDeclaration"] = false };
        var result = await session.RequestAsync(
            LspMethods.TextDocumentReferences,
            parameters,
            cancellationToken
        );
        return new FoundReferences(result.FindLocations(paths, skipGenerated: true), Written: null);
    }

    private async Task<Result<ToolResult>> AccessAsync(
        LspRequest request,
        FoundReferences found,
        CancellationToken cancellationToken
    )
    {
        var writes = request.Action is LspAction.Writes;
        HashSet<(string File, SourcePosition Position)> accessed;
        if (found.Written is { } written)
        {
            accessed = Accessed(found.Locations.Positions, written, writes);
        }
        else
        {
            var highlighted = await HighlightedAsync(
                found.Locations.Positions,
                writes,
                cancellationToken
            );
            if (highlighted.Error is { } error)
            {
                return error;
            }

            accessed = highlighted.Value;
        }

        var matching = found.Locations with
        {
            Positions = [.. found.Locations.Positions.Where(accessed.Contains)],
        };
        var locations = matching.Page(request.Limit, request.Offset) with
        {
            Access = request.Action,
        };
        return request.Context ? SourceLines.Add(locations, paths) : locations;
    }

    private HashSet<(string File, SourcePosition Position)> Accessed(
        IReadOnlyList<(string File, SourcePosition Position)> references,
        HashSet<(string File, SourcePosition Position)> written,
        bool writes
    )
    {
        var lines = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        return
        [
            .. references.Where(reference =>
                paths.ResultFilePath(reference.File) is { } file
                && Accesses(
                    writes,
                    written.Contains(reference)
                        ? ByRefAccess.Of(LinesOf(file), reference.Position)
                        : null
                )
            ),
        ];

        string[]? LinesOf(string file)
        {
            if (!lines.TryGetValue(file, out var text))
            {
                text = SourceLines.TryReadLines(file);
                lines[file] = text;
            }

            return text;
        }
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
        var lines = SourceLines.TryReadLines(paths.ResultFilePath(file));
        var reads = highlights
            .ToHighlightStarts(ReadHighlight)
            .Where(_ => Accesses(writes, written: null));
        var written = highlights
            .ToHighlightStarts(WriteHighlight)
            .Where(start => Accesses(writes, ByRefAccess.Of(lines, start)));
        return reads.Concat(written).Select(start => (file, start)).ToList();
    }

    /// <summary>
    /// Roslyn counts an argument passed by reference as a write, so its text decides whether it also reads.
    /// </summary>
    /// <param name="written">How a written reference uses the value, or null for a read.</param>
    private static bool Accesses(bool writes, ByRefUse? written) =>
        writes
            ? written is not (null or ByRefUse.Read)
            : written is null or ByRefUse.Read or ByRefUse.ReadWrite;

    /// <param name="Written">The written references, or null when documentHighlight must tell them apart.</param>
    private sealed record FoundReferences(
        FoundLocations Locations,
        HashSet<(string File, SourcePosition Position)>? Written
    );
}
