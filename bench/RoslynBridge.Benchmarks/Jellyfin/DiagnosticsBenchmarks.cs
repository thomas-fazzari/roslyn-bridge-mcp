// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;
using static RoslynBridge.Benchmarks.Jellyfin.JellyfinSymbols;

namespace RoslynBridge.Benchmarks.Jellyfin;

/// <summary>
/// Diagnostics of unchanged files.
/// </summary>
/// <remarks>
/// Roslyn reuses its reports. A first call takes seconds of analyzer work that bridge changes
/// cannot move. Editing before each call would modify the repository.
/// </remarks>
public class DiagnosticsBenchmarks : JellyfinBenchmarks
{
    // First page of 237 diagnostics (250 exceed the result size cap)
    [Benchmark]
    public Task<string> File() =>
        Session.CallAsync(
            "diagnostics",
            ("file", EncodingHelperFile),
            ("severity", "information"),
            ("limit", 100)
        );

    // 1,004 diagnostics in 169 files
    [Benchmark]
    public Task<string> ProjectSummary() =>
        Session.CallAsync(
            "diagnostics",
            ("file", ControllerFiles),
            ("severity", "hint"),
            ("summary", true)
        );
}
