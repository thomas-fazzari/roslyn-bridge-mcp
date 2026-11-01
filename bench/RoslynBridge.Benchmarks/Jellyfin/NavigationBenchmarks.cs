// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;
using static RoslynBridge.Benchmarks.Jellyfin.JellyfinSymbols;

namespace RoslynBridge.Benchmarks.Jellyfin;

public class NavigationBenchmarks : JellyfinBenchmarks
{
    private const int Limit = 250;

    private static readonly Dictionary<string, object?>[] _bulkQueries =
    [
        .. new[] { "definition", "references", "callers" }.Select(action => new Dictionary<
            string,
            object?
        >(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["symbol"] = GetItemById,
        }),
    ];

    [Benchmark]
    public Task<string> DefinitionAtPosition() =>
        Session.CallAsync(
            "navigate",
            ("action", "definition"),
            ("file", GetItemByIdFile),
            ("line", GetItemByIdLine),
            ("character", GetItemByIdCharacter)
        );

    [Benchmark]
    public Task<string> Definition() => Navigate("definition", GetItemById);

    [Benchmark]
    public Task<string> References() => Navigate("references", GetItemById);

    [Benchmark]
    public Task<string> Callers() => Navigate("callers", GetItemById);

    [Benchmark]
    public Task<string> Reads() => Navigate("reads", BaseItemId);

    [Benchmark]
    public Task<string> Writes() => Navigate("writes", BaseItemId);

    [Benchmark]
    public Task<string> Callees() => Navigate("callees", DeleteItem);

    [Benchmark]
    public Task<string> Source() => Navigate("source", DeleteItem);

    [Benchmark]
    public Task<string> Members() => Navigate("members", BaseItem);

    [Benchmark]
    public Task<string> Subtypes() => Navigate("subtypes", BaseItem);

    [Benchmark]
    public Task<string> Impact() => Navigate("impact", GetItemById);

    [Benchmark]
    public Task<string> Bulk() =>
        Session.CallAsync("bulk_navigate", ("queries", _bulkQueries), ("limit", Limit));

    private Task<string> Navigate(string action, string symbol) =>
        Session.CallAsync("navigate", ("action", action), ("symbol", symbol), ("limit", Limit));
}
