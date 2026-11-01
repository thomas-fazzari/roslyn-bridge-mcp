#!/usr/bin/env bash
#MISE description="Benchmarks BENCH_HEAD (this checkout when empty) against BENCH_BASELINE (master by default, empty for none), with extra arguments passed to BenchmarkDotNet"
#MISE depends=["bench:fixtures"]
set -euo pipefail

output="$PWD/artifacts/bench"
head="${BENCH_HEAD:-}"
baseline="${BENCH_BASELINE-master}"
worktrees=()

remove_worktrees() {
  local worktree
  # macOS bash 3.2 rejects an empty array under set -u
  for worktree in ${worktrees[@]+"${worktrees[@]}"}; do
    git worktree remove --force "$worktree"
  done
}
trap remove_worktrees EXIT

# Builds a git ref in a temporary worktree, or this checkout when the ref is empty
publish() {
  local ref="$1" destination="$output/$2" source=.
  if [[ -n "$ref" ]]; then
    source="$output/$2-source"
    git worktree remove --force "$source" 2>/dev/null || true
    git worktree add --quiet --detach "$source" "$ref"
    worktrees+=("$source")
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
