#!/usr/bin/env bash
#MISE description="Publishes the tested executables and license notices to GitHub"
set -euo pipefail

# The native jobs upload DOTNET_RUNTIME_NOTICES.txt with the executables
cp LICENSE NOTICE THIRD_PARTY_NOTICES.md artifacts/
notices=(LICENSE NOTICE THIRD_PARTY_NOTICES.md DOTNET_RUNTIME_NOTICES.txt)

cd artifacts
sha256sum roslyn-bridge-mcp-* "${notices[@]}" >SHA256SUMS

gh release create "$RELEASE_TAG" roslyn-bridge-mcp-* "${notices[@]}" SHA256SUMS --verify-tag --generate-notes
