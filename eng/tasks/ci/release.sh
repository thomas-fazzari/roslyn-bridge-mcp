#!/usr/bin/env bash
#MISE description="Publishes the GitHub release with the tested tool package"
set -euo pipefail

gh release create "$RELEASE_TAG" artifacts/nupkg/*.nupkg --verify-tag --generate-notes
