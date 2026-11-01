// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Benchmarks.Jellyfin;

public abstract class JellyfinBenchmarks : BridgeBenchmarks
{
    internal const string RepositoryName = "jellyfin";

    private protected override string Repository => RepositoryName;
}
