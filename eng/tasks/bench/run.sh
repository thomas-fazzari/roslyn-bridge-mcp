#!/usr/bin/env bash
#MISE description="Benchmarks BENCH_HEAD (this checkout when empty) against BENCH_BASELINE (master by default, empty for none), with extra arguments passed to BenchmarkDotNet"
#MISE depends=["bench:fixtures"]
set -euo pipefail

output="$PWD/artifacts/bench"
head="${BENCH_HEAD:-}"
baseline="${BENCH_BASELINE-master}"
# Outside the repository, where BenchmarkDotNet would find a second copy of its project.
# Resolved, because MSBuild names embedded resources wrongly under a symlink such as macOS /var.
sources="$(cd "$(mktemp -d)" && pwd -P)"

remove_sources() {
  local source
  for source in "$sources"/*; do
    if [[ -d "$source" ]]; then
      git worktree remove --force "$source"
    fi
  done
  rm -rf "$sources"
}
trap remove_sources EXIT

# Builds a git ref in a temporary worktree, or this checkout when the ref is empty
publish() {
  local ref="$1" destination="$output/$2" source=.
  if [[ -n "$ref" ]]; then
    source="$sources/$2"
    git worktree add --quiet --detach "$source" "$ref"
  fi
  dotnet publish "$source/src/RoslynBridge/RoslynBridge.csproj" --configuration Release \
    --output "$destination" --verbosity quiet
}

publish "$head" head
export ROSLYN_BRIDGE_BENCH_EXECUTABLE="$output/head/RoslynBridge"
export ROSLYN_BRIDGE_BENCH_REPOS="$PWD/bench/repos"
export ROSLYN_BRIDGE_BENCH_LOGS="$output/logs"

if [[ -n "$baseline" ]]; then
  publish "$baseline" baseline
  export ROSLYN_BRIDGE_BENCH_BASELINE="$output/baseline/RoslynBridge"
fi

rm -rf "$ROSLYN_BRIDGE_BENCH_LOGS" "$output/results"
if [[ $# -eq 0 ]]; then
  set -- --filter '*'
fi
dotnet run --project bench/RoslynBridge.Benchmarks --configuration Release -- \
  --artifacts "$output/results" "$@"
