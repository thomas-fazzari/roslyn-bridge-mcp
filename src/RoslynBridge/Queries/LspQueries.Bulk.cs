// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    private const int MaximumConcurrentQueries = 4;

    /// <param name="fileErrors">The errors of query files that match several files, by name.</param>
    public async Task<Result<ToolResult>> BulkAsync(
        BulkRequest request,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        var queries = request.Queries;
        var located = await LocateAllAsync(queries, fileErrors, cancellationToken);

        return await SectionsAsync(
            [.. queries.Select((query, index) => Label(index + 1, query))],
            async (index, token) =>
                located[index] switch
                {
                    ToolError error => error,
                    NavigationRequest query => await NavigationAsync(query, token),
                },
            cancellationToken
        );
    }

    private static async Task<SectionsReport> SectionsAsync(
        IReadOnlyList<string> labels,
        Func<int, CancellationToken, Task<Result<ToolResult>>> run,
        CancellationToken cancellationToken
    )
    {
        var results = await RunConcurrentlyAsync(labels.Count, run, cancellationToken);
        return new SectionsReport([
            .. labels.Zip(results, (label, result) => new ReportSection(label, result)),
        ]);
    }

    private static async Task<Result<T>[]> RunConcurrentlyAsync<T>(
        int count,
        Func<int, CancellationToken, Task<Result<T>>> run,
        CancellationToken cancellationToken
    )
    {
        var results = new Result<T>[count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = MaximumConcurrentQueries },
            async (index, _) =>
            {
                try
                {
                    results[index] = await run(index, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    results[index] = new ToolError(
                        ErrorCodes.Timeout,
                        "The query did not finish before the request timeout."
                    );
                }
            }
        );

        return results;
    }

    // A symbol that several queries name resolves once, and a failure to resolve it fails them all
    private async Task<Result<NavigationRequest>[]> LocateAllAsync(
        IReadOnlyList<NavigationRequest> queries,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        var named = queries
            .Where(query => NeedsLocation(query) && FileError(query, fileErrors) is null)
            .GroupBy(query => (query.Target.Symbol, query.Target.File))
            .ToList();
        var found = await RunConcurrentlyAsync(
            named.Count,
            (index, token) =>
            {
                var query = named[index].First();
                return LocateAsync(query.Target, query.Action, token);
            },
            cancellationToken
        );
        var locations = named.Zip(found).ToDictionary(pair => pair.First.Key, pair => pair.Second);
        return [.. queries.Select(query => Located(query, locations, fileErrors))];
    }

    private static Result<NavigationRequest> Located(
        NavigationRequest query,
        Dictionary<
            (string? Symbol, string? File),
            Result<(string File, SourcePosition Position)>
        > locations,
        IReadOnlyDictionary<string, ToolError> fileErrors
    )
    {
        if (FileError(query, fileErrors) is { } fileError)
        {
            return fileError;
        }

        if (!NeedsLocation(query))
        {
            return query;
        }

        if (
            locations[(query.Target.Symbol, query.Target.File)]
                .IsError(out var error, out var found)
        )
        {
            return error;
        }

        return query with
        {
            Target = SymbolTarget.At(found.File, found.Position),
        };
    }

    private static ToolError? FileError(
        NavigationRequest query,
        IReadOnlyDictionary<string, ToolError> fileErrors
    ) => query.Target.File is { } file ? fileErrors.GetValueOrDefault(file) : null;

    // source and members resolve declarations themselves
    private static bool NeedsLocation(NavigationRequest query) =>
        query.Target is { Symbol: not null, Line: null, Character: null }
        && query.Action is not (LspAction.Source or LspAction.Members);

    private static string Label(int number, NavigationRequest query) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{number} {query.Action.Name} {query.Target.Symbol ?? Position(query.Target)}"
        );

    private static string Position(SymbolTarget target) =>
        (target.Line, target.Character) switch
        {
            ({ } line, { } character) => string.Create(
                CultureInfo.InvariantCulture,
                $"{target.File}:{line}:{character}"
            ),
            ({ } line, null) => string.Create(
                CultureInfo.InvariantCulture,
                $"{target.File}:{line}"
            ),
            _ => target.File ?? string.Empty,
        };
}
