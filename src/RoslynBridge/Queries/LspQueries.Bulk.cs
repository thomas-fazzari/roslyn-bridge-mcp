// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    private const int MaximumConcurrentQueries = 4;

    /// <summary>
    /// Reports each query in request order, a failed one as its own section.
    /// </summary>
    /// <param name="fileErrors">The errors of the query files that match several files, by name.</param>
    public async Task<Result<ToolResult>> BulkAsync(
        LspRequest request,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        var queries = request
            .Queries.Select(query =>
                query with
                {
                    Limit = request.Limit,
                    Offset = request.Offset,
                    Context = request.Context,
                }
            )
            .ToList();
        var located = await LocateAllAsync(queries, fileErrors, cancellationToken);

        return await SectionsAsync(
            [.. queries.Select((query, index) => Label(index + 1, query))],
            async (index, token) =>
                located[index].Error is { } error
                    ? error
                    : await NavigationAsync(located[index].Value, token),
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
                catch (Exception exception) when (ToolFailures.IsReported(exception))
                {
                    results[index] = ToolFailures.ToError(exception);
                }
            }
        );

        return results;
    }

    // A symbol that several queries name resolves once, and a failure to resolve it fails them all
    private async Task<Result<LspRequest>[]> LocateAllAsync(
        IReadOnlyList<LspRequest> queries,
        IReadOnlyDictionary<string, ToolError> fileErrors,
        CancellationToken cancellationToken
    )
    {
        var named = queries
            .Where(query => NeedsLocation(query) && FileError(query, fileErrors) is null)
            .GroupBy(query => (query.Symbol, query.File))
            .ToList();
        var found = await RunConcurrentlyAsync(
            named.Count,
            (index, token) => LocateAsync(named[index].First(), token),
            cancellationToken
        );
        var locations = named.Zip(found).ToDictionary(pair => pair.First.Key, pair => pair.Second);
        return [.. queries.Select(query => Located(query, locations, fileErrors))];
    }

    private static Result<LspRequest> Located(
        LspRequest query,
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

        var location = locations[(query.Symbol, query.File)];
        if (location.Error is { } error)
        {
            return error;
        }

        var (file, position) = location.Value;
        return query with
        {
            Symbol = null,
            File = file,
            Line = position.Line,
            Character = position.Character,
        };
    }

    private static ToolError? FileError(
        LspRequest query,
        IReadOnlyDictionary<string, ToolError> fileErrors
    ) => query.File is { } file ? fileErrors.GetValueOrDefault(file) : null;

    // source and members resolve declarations themselves
    private static bool NeedsLocation(LspRequest query) =>
        query is { Symbol: not null, Line: null, Character: null }
        && query.Action is not (LspAction.Source or LspAction.Members);

    private static string Label(int number, LspRequest query) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{number} {query.Action.Name} {query.Symbol ?? Position(query)}"
        );

    private static string Position(LspRequest query) =>
        (query.Line, query.Character) switch
        {
            ({ } line, { } character) => string.Create(
                CultureInfo.InvariantCulture,
                $"{query.File}:{line}:{character}"
            ),
            ({ } line, null) => string.Create(CultureInfo.InvariantCulture, $"{query.File}:{line}"),
            _ => query.File ?? string.Empty,
        };
}
