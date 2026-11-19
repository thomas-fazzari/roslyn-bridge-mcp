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

    private async Task<Result<FoundReferences>> ReferencesAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        var lookup = await session.FindReferencesAsync(parameters, cancellationToken);
        if (lookup.IsError(out var error, out var references))
            return error;

        if (references.Found)
        {
            if (
                references
                    .Locations.FindLocations(paths, skipGenerated: true)
                    .IsError(out var readError, out var read)
            )
            {
                return readError;
            }

            if (
                references
                    .Written.FindLocations(paths, skipGenerated: true)
                    .IsError(out var writtenError, out var written)
            )
            {
                return writtenError;
            }

            HashSet<(string File, SourcePosition Position)> writtenPositions =
            [
                .. written.Positions,
            ];
            return new FoundReferences(
                read with
                {
                    Generated = references.Generated,
                },
                writtenPositions
            );
        }

        // definition already gives the declaration, which would read as one more usage
        parameters["context"] = new JsonObject { ["includeDeclaration"] = false };
        var response = await session.RequestAsync(
            LspMethods.TextDocumentReferences,
            parameters,
            cancellationToken
        );
        if (response.IsError(out var requestError, out var result))
            return requestError;

        var located = result.FindLocations(paths, skipGenerated: true);
        return located.Map(locations => new FoundReferences(locations, Written: null));
    }

    private async Task<Result<ToolResult>> AccessAsync(
        NavigationRequest request,
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
            if (highlighted.IsError(out var error, out var highlights))
                return error;

            accessed = highlights;
        }

        var matching = found.Locations with
        {
            Positions = [.. found.Locations.Positions.Where(accessed.Contains)],
        };
        var locations = matching.Page(request.Page.Limit, request.Page.Offset) with
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
        return results.All().Map(highlights => highlights.SelectMany(found => found).ToHashSet());
    }

    private async Task<Result<List<(string File, SourcePosition Position)>>> HighlightsAsync(
        string file,
        SourcePosition position,
        bool writes,
        CancellationToken cancellationToken
    )
    {
        var positioned = await PositionParametersAsync(file, position, cancellationToken);
        if (positioned.IsError(out var error, out var parameters))
            return error;

        var response = await session.RequestAsync(
            LspMethods.TextDocumentDocumentHighlight,
            parameters,
            cancellationToken
        );
        if (response.IsError(out var requestError, out var result))
            return requestError;

        if (result.ReadArray<LspHighlight>().IsError(out var readError, out var highlights))
            return readError;

        var lines = SourceLines.TryReadLines(paths.ResultFilePath(file));
        var reads = StartsOf(highlights, ReadHighlight);
        var written = StartsOf(highlights, WriteHighlight);
        return reads
            .Where(_ => Accesses(writes, written: null))
            .Concat(written.Where(start => Accesses(writes, ByRefAccess.Of(lines, start))))
            .Select(start => (file, start))
            .ToList();
    }

    private static IEnumerable<SourcePosition> StartsOf(LspHighlight[] highlights, int kind) =>
        highlights
            .Where(highlight => highlight.Kind == kind)
            .Select(highlight => highlight.Range.Start.OneBased);

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
