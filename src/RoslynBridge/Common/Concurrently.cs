// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Common;

internal static class Concurrently
{
    private const int MAXIMUM_CONCURRENT_QUERIES = 4;

    /// <summary>
    /// Returns one result per item, in item order. A query that the cancellation stops returns a timeout error, so the others keep their results.
    /// </summary>
    public static async Task<Result<T>[]> RunAsync<TItem, T>(
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<Result<T>>> run,
        CancellationToken cancellationToken
    )
    {
        var results = new Result<T>[items.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MAXIMUM_CONCURRENT_QUERIES },
            async (index, _) =>
            {
                try
                {
                    results[index] = await run(items[index], cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    results[index] = new ToolError(
                        ErrorCodes.TIMEOUT,
                        "The query did not finish before the request timeout."
                    );
                }
            }
        );

        return results;
    }

    /// <summary>
    /// Runs one query per section and reports each result under its label, in section order.
    /// </summary>
    public static async Task<SectionsReport> SectionsAsync<TItem>(
        IReadOnlyList<(string Label, TItem Item)> sections,
        Func<TItem, CancellationToken, Task<Result<ToolResult>>> run,
        CancellationToken cancellationToken
    )
    {
        var results = await RunAsync(
            sections,
            (section, token) => run(section.Item, token),
            cancellationToken
        );
        return new SectionsReport([
            .. sections.Zip(results, (section, result) => new ReportSection(section.Label, result)),
        ]);
    }
}
