// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;
using static RoslynBridge.Benchmarks.Jellyfin.JellyfinSymbols;

namespace RoslynBridge.Benchmarks.Jellyfin;

/// <summary>
/// Symbol search, and errors for names matching no single declaration.
/// </summary>
public class SearchBenchmarks : JellyfinBenchmarks
{
    private const int LIMIT = 250;

    [Benchmark]
    public Task<string> Query() =>
        Session.CallAsync("symbols", ("query", LIBRARY_MANAGER_QUERY), ("limit", LIMIT));

    [Benchmark]
    public Task<string> Outline() =>
        Session.CallAsync("symbols", ("file", ENCODING_HELPER_FILE), ("limit", LIMIT));

    [Benchmark]
    public Task<string> MissingMember() =>
        Unresolved("symbol_not_found", JellyfinSymbols.MISSING_MEMBER);

    [Benchmark]
    public Task<string> MisspelledType() =>
        Unresolved("symbol_not_found", JellyfinSymbols.MISSPELLED_TYPE);

    [Benchmark]
    public Task<string> AmbiguousName() => Unresolved("ambiguous_symbol", BARE_NAME);

    private Task<string> Unresolved(string errorCode, string symbol) =>
        Session.FailAsync(errorCode, "navigate", ("action", "definition"), ("symbol", symbol));
}
