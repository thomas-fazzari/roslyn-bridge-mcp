// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;
using static RoslynBridge.Benchmarks.Jellyfin.JellyfinSymbols;

namespace RoslynBridge.Benchmarks.Jellyfin;

public class NavigationBenchmarks : JellyfinBenchmarks
{
    private const int LIMIT = 250;

    private static readonly Dictionary<string, object?>[] _bulkQueries =
    [
        .. new[] { "definition", "references", "callers" }.Select(action => new Dictionary<
            string,
            object?
        >(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["symbol"] = GET_ITEM_BY_ID,
        }),
    ];

    [Benchmark]
    public Task<string> DefinitionAtPosition() =>
        Session.CallAsync(
            "navigate",
            ("action", "definition"),
            ("file", GET_ITEM_BY_ID_FILE),
            ("line", GET_ITEM_BY_ID_LINE),
            ("character", GET_ITEM_BY_ID_CHARACTER)
        );

    [Benchmark]
    public Task<string> Definition() => Navigate("definition", GET_ITEM_BY_ID);

    [Benchmark]
    public Task<string> References() => Navigate("references", GET_ITEM_BY_ID);

    [Benchmark]
    public Task<string> Callers() => Navigate("callers", GET_ITEM_BY_ID);

    [Benchmark]
    public Task<string> Reads() => Navigate("reads", BASE_ITEM_ID);

    [Benchmark]
    public Task<string> Writes() => Navigate("writes", BASE_ITEM_ID);

    [Benchmark]
    public Task<string> Callees() => Navigate("callees", DELETE_ITEM);

    [Benchmark]
    public Task<string> Source() => Navigate("source", DELETE_ITEM);

    [Benchmark]
    public Task<string> Members() => Navigate("members", BASE_ITEM);

    [Benchmark]
    public Task<string> Subtypes() => Navigate("subtypes", BASE_ITEM);

    [Benchmark]
    public Task<string> Impact() => Navigate("impact", GET_ITEM_BY_ID);

    [Benchmark]
    public Task<string> Bulk() =>
        Session.CallAsync("bulk_navigate", ("queries", _bulkQueries), ("limit", LIMIT));

    private Task<string> Navigate(string action, string symbol) =>
        Session.CallAsync("navigate", ("action", action), ("symbol", symbol), ("limit", LIMIT));
}
