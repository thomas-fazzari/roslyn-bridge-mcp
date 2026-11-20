// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Common;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using RoslynBridge.Symbols;
using RoslynBridge.Workspace;

namespace RoslynBridge.Features.Navigate;

internal sealed partial class NavigateHandler
{
    private const int ReadHighlight = 2;
    private const int WriteHighlight = 3;

    private async Task<Result<FoundReferences>> ReferencesAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    )
    {
        var lookup = await session.FindReferencesAsync(position, cancellationToken);
        if (lookup.IsError(out var error, out var references))
            return error;

        if (references.Found)
        {
            var reading = references.Locations.FindLocations(paths, skipGenerated: true);
            if (reading.IsError(out var readError, out var read))
                return readError;

            var writing = references.Written.FindLocations(paths, skipGenerated: true);
            return writing.IsError(out var writtenError, out var written)
                ? writtenError
                : new FoundReferences(
                    read with
                    {
                        Generated = references.Generated,
                    },
                    [.. written.Positions]
                );
        }

        // definition already gives the declaration, which would read as one more usage
        var response = await session.ReferencesAsync(
            position,
            includeDeclaration: false,
            cancellationToken
        );
        if (response.IsError(out var requestError, out var result))
            return requestError;

        var located = result.FindLocations(paths, skipGenerated: true);
        return located.Map(locations => new FoundReferences(locations, Written: null));
    }

    private async Task<Result<ToolResult>> AccessAsync(
        NavigateRequest request,
        FoundReferences found,
        CancellationToken cancellationToken
    )
    {
        var writes = request.Action is LspAction.Writes;
        HashSet<SourceLocation> accessed;
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
        var locations = matching.Page(request.Page) with { Access = request.Action };
        return request.Context ? locations.WithSourceLines() : locations;
    }

    private static HashSet<SourceLocation> Accessed(
        IReadOnlyList<SourceLocation> references,
        HashSet<SourceLocation> written,
        bool writes
    )
    {
        var lines = new SourceLines();
        return
        [
            .. references.Where(reference =>
                reference.File.WorkspaceFile is { } file
                && Accesses(
                    writes,
                    written.Contains(reference)
                        ? ByRefAccess.Of(lines.Of(file), reference.Position)
                        : null
                )
            ),
        ];
    }

    private async Task<Result<HashSet<SourceLocation>>> HighlightedAsync(
        IReadOnlyList<SourceLocation> references,
        bool writes,
        CancellationToken cancellationToken
    )
    {
        var firsts = references
            .Where(reference => reference.File.WorkspaceFile is not null)
            .GroupBy(reference => reference.File)
            .Select(group => group.First())
            .ToList();
        var results = await Concurrently.RunAsync(
            firsts,
            (first, token) => HighlightsAsync(first, writes, token),
            cancellationToken
        );
        return results.All().Map(highlights => highlights.SelectMany(found => found).ToHashSet());
    }

    private async Task<Result<List<SourceLocation>>> HighlightsAsync(
        SourceLocation reference,
        bool writes,
        CancellationToken cancellationToken
    )
    {
        var positioned = await locator.DocumentPositionAsync(reference, cancellationToken);
        if (positioned.IsError(out var error, out var target))
            return error;

        var response = await session.DocumentHighlightsAsync(target, cancellationToken);
        if (response.IsError(out var requestError, out var highlights))
            return requestError;

        var lines = SourceLines.TryReadLines(reference.File.WorkspaceFile);
        var reads = StartsOf(highlights, ReadHighlight);
        var written = StartsOf(highlights, WriteHighlight);
        return reads
            .Where(_ => Accesses(writes, written: null))
            .Concat(written.Where(start => Accesses(writes, ByRefAccess.Of(lines, start))))
            .Select(start => reference with { Position = start })
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
        HashSet<SourceLocation>? Written
    );
}
