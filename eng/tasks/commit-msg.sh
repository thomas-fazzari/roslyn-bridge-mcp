#!/usr/bin/env bash
# Checks a commit message file against the rules of CONTRIBUTING.md
set -euo pipefail

types='fix|feat|test|style|docs|build|ops|chore|perf|refactor'
max_length=50

# Git appends the diff below this line with `commit --verbose`
lines=()
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^#\ -+\ \>8\ -+$ ]] && break
  [[ "$line" == \#* ]] && continue
  lines+=("$line")
done <"$1"

subject="${lines[0]:-}"

# Git writes these itself, and a squash merge takes its title from the pull request
[[ "$subject" =~ ^(Merge|Revert|fixup!|squash!)\  ]] && exit 0

if ! [[ "$subject" =~ ^($types)!?:\ [^A-Z[:space:]] ]]; then
  printf "Start the commit message with a type (%s), then ': ', in lowercase. Add ! after the type for a breaking change.\n" "$types" >&2
  exit 1
fi

if ((${#subject} > max_length)); then
  printf 'Keep the first line within %d characters. It has %d.\n' "$max_length" "${#subject}" >&2
  exit 1
fi

for line in "${lines[@]:1}"; do
  if [[ "$line" =~ [^[:space:]] ]]; then
    printf 'Write only a title. Put the description and any trailer in the pull request.\n' >&2
    exit 1
  fi
done
