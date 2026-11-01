// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;
using static RoslynBridge.Benchmarks.Jellyfin.JellyfinSymbols;

namespace RoslynBridge.Benchmarks.Jellyfin;

/// <summary>
/// Edit previews.
/// </summary>
/// <remarks>
/// Applying would modify the repository.
/// </remarks>
public class EditBenchmarks : JellyfinBenchmarks
{
    // 50 files
    [Benchmark]
    public Task<string> RenamePreview() =>
        Session.CallAsync(
            "edit",
            ("action", "rename"),
            ("symbol", GetItemById),
            ("newName", "FindItemById")
        );
}
