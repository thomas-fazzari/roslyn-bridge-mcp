# v2.1.1 benchmarks

- Baseline: v2.1.0, measured in the same run
- Roslyn server: 5.13.0-1.26514.10
- Repository: jellyfin v12.4
- Date: 2026-11-01
- Raw measurements: [results.json](results.json)

```

BenchmarkDotNet v0.15.8, macOS 27.0 (26A428) [Darwin 27.0.0]
Apple M4 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  base   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  head   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

LaunchCount=3  RunStrategy=Monitoring  UnrollFactor=1

```
| Type                  | Method               | Job  | InvocationCount | IterationCount | WarmupCount | Mean           | StdDev       | Median         | Max            | P95            | Median ratio | MannWhitney(10%) |
|---------------------- |--------------------- |----- |---------------- |--------------- |------------ |---------------:|-------------:|---------------:|---------------:|---------------:|-------------:|----------------- |
| DiagnosticsBenchmarks | File                 | base | Default         | 20             | 3           |     1,186.0 μs |     433.4 μs |     1,080.9 μs |     3,068.0 μs |     1,443.5 μs |         1.00 | Baseline         |
| DiagnosticsBenchmarks | File                 | head | Default         | 20             | 3           |     1,262.2 μs |     634.4 μs |     1,117.9 μs |     4,236.6 μs |     1,499.2 μs |         1.03 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| EditBenchmarks        | RenamePreview        | base | Default         | 20             | 3           | 1,398,870.7 μs | 470,008.1 μs | 1,197,750.4 μs | 2,115,857.4 μs | 2,015,485.9 μs |         1.00 | Baseline         |
| EditBenchmarks        | RenamePreview        | head | Default         | 20             | 3           | 1,422,733.4 μs | 473,490.8 μs | 1,216,670.2 μs | 2,116,130.8 μs | 2,061,774.5 μs |         1.02 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | DefinitionAtPosition | base | Default         | 20             | 3           |     1,097.5 μs |     924.1 μs |       818.0 μs |     5,022.1 μs |     3,365.2 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | DefinitionAtPosition | head | Default         | 20             | 3           |       957.6 μs |     561.0 μs |       824.4 μs |     3,381.0 μs |     1,186.0 μs |         1.01 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| SearchBenchmarks      | Query                | base | Default         | 20             | 3           |     7,608.6 μs |     917.7 μs |     7,405.4 μs |    10,877.9 μs |     9,500.1 μs |         1.00 | Baseline         |
| SearchBenchmarks      | Query                | head | Default         | 20             | 3           |     7,678.6 μs |     894.1 μs |     7,420.8 μs |    11,067.8 μs |     9,465.4 μs |         1.00 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| DiagnosticsBenchmarks | ProjectSummary       | base | Default         | 20             | 3           |    51,247.1 μs |   7,050.6 μs |    48,926.0 μs |    70,193.3 μs |    66,644.8 μs |         1.00 | Baseline         |
| DiagnosticsBenchmarks | ProjectSummary       | head | Default         | 20             | 3           |    54,767.6 μs |   4,480.4 μs |    52,844.9 μs |    71,024.4 μs |    60,380.8 μs |         1.08 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Definition           | base | Default         | 20             | 3           |     6,328.6 μs |   1,481.5 μs |     5,862.3 μs |    15,053.0 μs |     9,137.2 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Definition           | head | Default         | 20             | 3           |     6,423.1 μs |     944.5 μs |     6,139.3 μs |     9,586.2 μs |     9,266.9 μs |         1.05 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| SearchBenchmarks      | Outline              | base | Default         | 20             | 3           |     6,694.6 μs |     678.9 μs |     6,488.3 μs |     9,998.8 μs |     7,592.2 μs |         1.00 | Baseline         |
| SearchBenchmarks      | Outline              | head | Default         | 20             | 3           |     6,637.7 μs |     570.7 μs |     6,502.2 μs |     9,020.0 μs |     7,770.4 μs |         1.00 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | References           | base | Default         | 20             | 3           |   510,686.0 μs |   1,868.7 μs |   510,511.5 μs |   518,392.6 μs |   514,001.7 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | References           | head | Default         | 20             | 3           |    13,848.6 μs |   2,093.7 μs |    13,478.3 μs |    26,479.3 μs |    16,903.7 μs |         0.03 | Faster           |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| SearchBenchmarks      | MissingMember        | base | Default         | 20             | 3           |    22,662.8 μs |   2,273.7 μs |    22,683.6 μs |    27,364.1 μs |    27,178.7 μs |         1.00 | Baseline         |
| SearchBenchmarks      | MissingMember        | head | Default         | 20             | 3           |    22,377.2 μs |   2,095.6 μs |    22,397.4 μs |    28,554.1 μs |    25,613.2 μs |         0.99 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Callers              | base | Default         | 20             | 3           |    55,726.9 μs |   5,620.4 μs |    53,858.1 μs |    79,272.2 μs |    65,256.0 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Callers              | head | Default         | 20             | 3           |    54,395.5 μs |   3,152.1 μs |    54,001.7 μs |    65,884.5 μs |    61,073.4 μs |         1.00 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| SearchBenchmarks      | MisspelledType       | base | Default         | 20             | 3           |    19,789.2 μs |   1,330.5 μs |    19,754.9 μs |    24,007.0 μs |    22,408.4 μs |         1.00 | Baseline         |
| SearchBenchmarks      | MisspelledType       | head | Default         | 20             | 3           |    19,323.3 μs |   1,299.4 μs |    18,930.0 μs |    22,282.3 μs |    21,568.6 μs |         0.96 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Reads                | base | Default         | 20             | 3           |   605,058.6 μs |  34,131.8 μs |   598,092.3 μs |   750,659.0 μs |   664,612.1 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Reads                | head | Default         | 20             | 3           |    43,295.7 μs |  15,777.4 μs |    38,849.8 μs |   123,380.2 μs |    50,822.3 μs |         0.06 | Faster           |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| SearchBenchmarks      | AmbiguousName        | base | Default         | 20             | 3           |    53,132.8 μs |   4,654.8 μs |    52,474.2 μs |    70,507.8 μs |    62,898.3 μs |         1.00 | Baseline         |
| SearchBenchmarks      | AmbiguousName        | head | Default         | 20             | 3           |    52,536.2 μs |   4,790.9 μs |    51,486.0 μs |    68,019.6 μs |    63,457.2 μs |         0.98 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Writes               | base | Default         | 20             | 3           |   595,990.6 μs |  25,033.5 μs |   593,988.1 μs |   710,818.5 μs |   620,791.5 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Writes               | head | Default         | 20             | 3           |    42,638.9 μs |  15,191.9 μs |    39,321.9 μs |   120,740.2 μs |    50,474.4 μs |         0.07 | Faster           |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Callees              | base | Default         | 20             | 3           |   115,998.0 μs |   9,238.9 μs |   112,256.0 μs |   145,534.6 μs |   132,552.5 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Callees              | head | Default         | 20             | 3           |   112,311.6 μs |   6,588.4 μs |   109,919.6 μs |   132,253.0 μs |   125,894.8 μs |         0.98 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Source               | base | Default         | 20             | 3           |     9,692.6 μs |   1,002.5 μs |     9,428.1 μs |    13,244.5 μs |    11,805.6 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Source               | head | Default         | 20             | 3           |     9,448.0 μs |     954.8 μs |     9,227.1 μs |    13,390.2 μs |    11,290.6 μs |         0.98 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Members              | base | Default         | 20             | 3           |    12,033.7 μs |     862.9 μs |    11,852.0 μs |    14,536.4 μs |    13,623.0 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Members              | head | Default         | 20             | 3           |    12,479.4 μs |   1,088.1 μs |    12,463.9 μs |    15,228.9 μs |    14,581.5 μs |         1.05 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Subtypes             | base | Default         | 20             | 3           |    13,954.0 μs |   1,457.2 μs |    13,743.5 μs |    18,247.4 μs |    16,264.7 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Subtypes             | head | Default         | 20             | 3           |    14,499.7 μs |   1,217.2 μs |    14,675.0 μs |    16,709.7 μs |    16,678.5 μs |         1.07 | Same             |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Impact               | base | Default         | 20             | 3           |   512,390.6 μs |   2,523.8 μs |   511,635.6 μs |   522,845.7 μs |   516,005.4 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Impact               | head | Default         | 20             | 3           |    56,796.9 μs |   2,502.4 μs |    56,423.9 μs |    66,505.7 μs |    60,324.4 μs |         0.11 | Faster           |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| NavigationBenchmarks  | Bulk                 | base | Default         | 20             | 3           |   511,756.1 μs |   1,585.2 μs |   511,598.4 μs |   518,220.4 μs |   514,622.0 μs |         1.00 | Baseline         |
| NavigationBenchmarks  | Bulk                 | head | Default         | 20             | 3           |    56,481.7 μs |   3,107.6 μs |    55,487.3 μs |    68,274.4 μs |    62,379.2 μs |         0.11 | Faster           |
|                       |                      |      |                 |                |             |                |              |                |                |                |              |                  |
| StartupBenchmarks     | Load                 | base | 1               | 5              | 1           | 4,314,343.6 μs | 126,938.0 μs | 4,302,569.5 μs | 4,505,724.3 μs | 4,488,977.7 μs |         1.00 | Baseline         |
| StartupBenchmarks     | Load                 | head | 1               | 5              | 1           | 4,365,823.7 μs | 163,476.2 μs | 4,310,914.8 μs | 4,764,122.2 μs | 4,628,442.7 μs |         1.00 | Same             |
