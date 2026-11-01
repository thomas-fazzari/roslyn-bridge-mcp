// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using BenchmarkDotNet.Running;
using RoslynBridge.Benchmarks;

var summaries = BenchmarkSwitcher
    .FromAssembly(typeof(BenchmarkConfig).Assembly)
    .Run(args, BenchmarkConfig.Create());
return summaries.SelectMany(summary => summary.Reports).Any(report => !report.Success) ? 1 : 0;
