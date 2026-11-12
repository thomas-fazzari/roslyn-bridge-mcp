# Benchmarks

The benchmarks in `bench/RoslynBridge.Benchmarks` measure tool calls end to end over MCP with [BenchmarkDotNet](https://benchmarkdotnet.org). They compare two builds of the bridge on real repositories, to prove a gain or catch a regression. They run on a local machine only, because shared CI runners are too noisy to detect a 10% change.

The results of each release that changed performance are in [results](results).

## Running

```sh
mise run bench:run
```

The task clones the repositories of `bench/fixtures.tsv` into `bench/repos`, publishes this checkout as `head` and `master` as `base`, then runs every benchmark on both. Set `BENCH_BASELINE` to another git ref, or to an empty value to measure `head` alone. Arguments after `--` go to BenchmarkDotNet:

```sh
BENCH_BASELINE=v2.1.0 mise run bench:run -- --filter '*Navigation*'
```

A full run takes a while. Filter on the area a change touches to get a faster answer.

## Methodology

Each benchmark starts a bridge on a repository, waits for the workspace to load, then times one tool call per iteration. A call does not reach a steady state the way a CPU loop does, so the job uses the `Monitoring` strategy and reports each call on its own.

- **Launches.** Each launch starts a new bridge and reloads the workspace. The spread therefore includes the variance between two loads.
- **Warmup.** The first calls of each launch fill Roslyn's caches and run its analyzers. They are not counted, so the results describe a warm server. `StartupBenchmarks` measures the load itself.
- **Same run.** `base` and `head` run in the same session, one after the other, under the same machine conditions. Numbers from two different runs are not comparable.
- **Same server.** Both builds start the `roslyn-language-server` on `PATH`. A comparison therefore measures the bridge, even across tags built for different server versions.

Memory is not measured, because BenchmarkDotNet only sees its own process, not the bridge or the Roslyn server.

## Reading the results

BenchmarkDotNet writes a Markdown table and a JSON file per class to `artifacts/bench/results/results`. The columns that matter:

| Column             | Meaning                                                                                     |
| ------------------ | ------------------------------------------------------------------------------------------- |
| `Median`           | The typical duration of a call. Compare builds on this column.                              |
| `P95`              | The duration that 95% of calls stay under. A slow tail shows here before the median moves.  |
| `Max`              | The slowest call. Often a single outlier from garbage collection or Roslyn background work. |
| `Median ratio`     | `head` median divided by `base` median. Below 1 means `head` is faster.                     |
| `MannWhitney(10%)` | `Faster` or `Slower` when the gap is over 10% and statistically significant, else `Same`.   |

Trust the `MannWhitney(10%)` verdict over the ratio. Two identical builds can differ by more than 10% in median, and the test then still says `Same`. A ratio far from 1 next to `Same` is noise.

A benchmark shows `NA` when a call failed. The exception is in the BenchmarkDotNet log, in `artifacts/bench/results`. Each bridge also writes its log to `artifacts/bench/logs`, in a file named after the benchmark and the job.

## Release results

When a published release changes performance, run its benchmarks against the previous tag:

```sh
mise run bench:release v2.2.0
```

The task builds both tags, runs every benchmark and writes `bench/results/v2.2.0`. Its `README.md` names the baseline, the Roslyn server version and the repository tags, followed by the joined table and the machine description. The raw measurements are in `results.json`. Set `BENCH_BASELINE` to compare against another tag. Commit the folder in its own pull request. The release never waits for it.
