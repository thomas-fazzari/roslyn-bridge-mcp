#!/usr/bin/env bash
#MISE description="Copies the native executable and the runtime notices to their release asset names"
set -euo pipefail

extension=""
if [[ "$EXECUTABLE" == *.exe ]]; then
  extension=".exe"
fi

mkdir -p artifacts/release
cp "artifacts/native/$EXECUTABLE" "artifacts/release/roslyn-bridge-mcp-$RUNTIME_ID$extension"
cp artifacts/native/DOTNET_RUNTIME_NOTICES.txt artifacts/release/
