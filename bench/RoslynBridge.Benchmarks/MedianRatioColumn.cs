// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace RoslynBridge.Benchmarks;

/// <summary>
/// Median of a build divided by the baseline median.
/// </summary>
/// <remarks>
/// The built-in Ratio averages per-sample ratios, so slow outliers skew it.
/// </remarks>
internal sealed class MedianRatioColumn : BaselineCustomColumn
{
    public static MedianRatioColumn Instance { get; } = new();

    public override string Id => nameof(MedianRatioColumn);

    public override string ColumnName => "Median ratio";

    public override int PriorityInCategory => 0;

    public override bool IsNumeric => true;

    public override UnitType UnitType => UnitType.Dimensionless;

    public override string Legend => "Median of this build divided by the median of the baseline";

    public override string GetValue(
        Summary summary,
        BenchmarkCase benchmarkCase,
        Statistics baseline,
        IReadOnlyDictionary<string, Metric> baselineMetrics,
        Statistics current,
        IReadOnlyDictionary<string, Metric> currentMetrics,
        bool isBaseline
    ) => (current.Median / baseline.Median).ToString("0.00", CultureInfo.InvariantCulture);
}
