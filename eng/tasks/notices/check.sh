#!/usr/bin/env bash
#MISE description="Checks that THIRD_PARTY_NOTICES.md lists the packages the tool package ships"
set -euo pipefail
export LC_ALL=C

notices=THIRD_PARTY_NOTICES.md

json=$(dotnet list src/RoslynBridge package --include-transitive --format json)

# Only packages have an "id" property. Analyzers run at build time and are not part of the package
shipped=$(grep -o '"id": "[^"]*"' <<<"$json" | cut -d '"' -f 4 |
  grep -vxF -e Meziantou.Analyzer -e Roslynator.Analyzers | sort -u) || true

# Each package is a "- `Id`: ..." line under the "NuGet packages" heading
listed=$(awk '
  /^## / { in_packages = ($0 ~ /^## NuGet packages[[:space:]]*$/) }
  in_packages && match($0, /^- `[^`]+`/) { print substr($0, 4, RLENGTH - 4) }
' "$notices" | sort -u)

if [[ -z "$shipped" || -z "$listed" ]]; then
  echo "Found no packages to compare." >&2
  exit 1
fi

problems=$(
  comm -23 <(echo "$shipped") <(echo "$listed") | sed "s/^/Missing from $notices: /"
  comm -13 <(echo "$shipped") <(echo "$listed") | sed "s/^/Listed in $notices but not shipped: /"
)
if [[ -n "$problems" ]]; then
  echo "$problems" >&2
  exit 1
fi

echo "$notices lists all $(wc -l <<<"$shipped" | tr -d ' ') shipped packages."
