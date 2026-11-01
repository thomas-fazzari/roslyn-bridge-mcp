#!/usr/bin/env bash
#MISE description="Clones the benchmark repositories at the tags in bench/fixtures.tsv and restores them"
set -euo pipefail

repos=bench/repos
mkdir -p "$repos"
while IFS=$'\t' read -r name url tag solution; do
  if [[ -z "$name" || "$name" == \#* ]]; then
    continue
  fi

  target="$repos/$name"
  if [[ ! -d "$target/.git" ]]; then
    git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" "$url" "$target"
  elif [[ "$(git -C "$target" describe --tags --exact-match 2>/dev/null)" != "$tag" ]]; then
    git -C "$target" fetch --quiet --depth 1 origin tag "$tag"
    git -C "$target" checkout --quiet --force --detach "tags/$tag"
  fi

  dotnet restore "$target/$solution" --verbosity quiet
done <bench/fixtures.tsv
