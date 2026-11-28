// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;

namespace RoslynBridge.Benchmarks;

internal static class BenchmarkConfig
{
    internal const string BASELINE_VARIABLE = "ROSLYN_BRIDGE_BENCH_BASELINE";

    // Each launch reloads the workspace (launch-to-launch variance)
    private const int LAUNCHES = 3;
    private const int WARMUPS = 3;
    private const int ITERATIONS = 20;

    // Finer thresholds claim a precision runs lack (identical builds differed by up to 16% in median)
    private const string SIGNIFICANT_CHANGE = "10%";

    /// <summary>
    /// Monitoring job with one timed call per iteration.
    /// </summary>
    /// <remarks>
    /// Calls have no steady state. With a baseline, a Mann-Whitney test marks each benchmark
    /// faster, slower or same.
    /// </remarks>
    public static IConfig Create()
    {
        var job = Job
            .Default.WithStrategy(RunStrategy.Monitoring)
            .WithLaunchCount(LAUNCHES)
            .WithWarmupCount(WARMUPS)
            .WithIterationCount(ITERATIONS);

        var config = ManualConfig
            .CreateEmpty()
            .AddLogger(ConsoleLogger.Default)
            .AddValidator([.. DefaultConfig.Instance.GetValidators()])
            .AddAnalyser([.. DefaultConfig.Instance.GetAnalysers()])
            .AddColumnProvider(DefaultColumnProviders.Instance)
            // P95 is also what BenchmarkDotNet adds on its own to some tables, so all tables match
            .AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Max)
            // Mean confidence interval (meaningless with slow outliers)
            .HideColumns(Column.Error)
            .AddExporter(MarkdownExporter.GitHub, JsonExporter.FullCompressed)
            // Joined tables share one unit, which defaults to the smallest one (μs)
            .WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond));

        if (Environment.GetEnvironmentVariable(BASELINE_VARIABLE) is not { Length: > 0 } baseline)
        {
            return config.AddJob(job.AsDefault());
        }

        var head = BridgeSession.RequiredVariable(BridgeSession.EXECUTABLE_VARIABLE);
        return config
            .AddJob(
                job.WithEnvironmentVariable(BridgeSession.EXECUTABLE_VARIABLE, baseline)
                    .WithId("base")
                    .AsBaseline(),
                job.WithEnvironmentVariable(BridgeSession.EXECUTABLE_VARIABLE, head).WithId("head")
            )
            .AddColumn(MedianRatioColumn.Instance, StatisticalTestColumn.Create(SIGNIFICANT_CHANGE))
            .HideColumns(Column.EnvironmentVariables, Column.Ratio, Column.RatioSD);
    }
}
