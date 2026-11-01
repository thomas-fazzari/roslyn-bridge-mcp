#!/usr/bin/env bash
#MISE description="Benchmarks a release tag against the previous tag (or BENCH_BASELINE) and writes bench/results/<tag>"
#MISE depends=["bench:fixtures"]
set -euo pipefail

tag="${1:?Usage: mise run bench:release <tag>}"
baseline="${BENCH_BASELINE:-$(git describe --tags --abbrev=0 "$tag^")}"
results=artifacts/bench/results/results
target="bench/results/$tag"

BENCH_HEAD="$tag" BENCH_BASELINE="$baseline" eng/tasks/bench/run.sh --filter '*' --join

reports=("$results"/*-report-github.md)
measurements=("$results"/*-report-full-compressed.json)
if [[ ${#reports[@]} -ne 1 || ${#measurements[@]} -ne 1 ]]; then
  printf 'Expected one joined report in %s.\n' "$results" >&2
  exit 1
fi

# The bridge runs the server on PATH, whose tool store path names its version
server="$(realpath "$(command -v roslyn-language-server)")"
if [[ "$server" =~ /roslyn-language-server/([^/]+)/ ]]; then
  server="${BASH_REMATCH[1]}"
fi
mkdir -p "$target"
cp "${measurements[0]}" "$target/results.json"
{
  printf '# %s benchmarks\n\n' "$tag"
  printf -- '- Baseline: %s, measured in the same run\n' "$baseline"
  printf -- '- Roslyn server: %s\n' "$server"
  while IFS=$'\t' read -r name _ fixture _; do
    if [[ -n "$name" && "$name" != \#* ]]; then
      printf -- '- Repository: %s %s\n' "$name" "$fixture"
    fi
  done <bench/fixtures.tsv
  printf -- '- Date: %s\n' "$(date -u +%Y-%m-%d)"
  printf -- '- Raw measurements: [results.json](results.json)\n\n'
  cat "${reports[0]}"
} >"$target/README.md"
printf 'Wrote %s\n' "$target"
