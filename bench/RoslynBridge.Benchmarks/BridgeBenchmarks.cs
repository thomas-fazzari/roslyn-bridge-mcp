// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;

namespace RoslynBridge.Benchmarks;

/// <summary>
/// Starts one bridge per benchmark process.
/// </summary>
/// <remarks>
/// The workspace is loaded before any timed call.
/// </remarks>
public abstract class BridgeBenchmarks : IAsyncDisposable
{
    private protected BridgeSession Session { get; } = new();

    /// <summary>
    /// Repository name in bench/fixtures.tsv.
    /// </summary>
    private protected abstract string Repository { get; }

    [GlobalSetup]
    public Task StartAsync() => Session.StartAsync(Repository);

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
