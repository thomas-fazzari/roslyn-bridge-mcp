// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Attributes;

namespace RoslynBridge.Benchmarks.Jellyfin;

/// <summary>
/// From process start to a loaded workspace.
/// </summary>
/// <remarks>
/// Shutdown runs outside the timed call.
/// </remarks>
[WarmupCount(1)]
[IterationCount(5)]
public class StartupBenchmarks : IAsyncDisposable
{
    private BridgeSession? _session;

    [Benchmark]
    public Task Load()
    {
        _session = new BridgeSession();
        return _session.StartAsync(JellyfinBenchmarks.RepositoryName);
    }

    // BenchmarkDotNet only runs a synchronous iteration cleanup
    [IterationCleanup]
    public void Stop() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        GC.SuppressFinalize(this);
    }
}
