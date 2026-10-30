#!/usr/bin/env bash
# Installs the latest RoslynBridge release, then runs its setup.
# Usage: curl -fsSL https://raw.githubusercontent.com/thomas-fazzari/roslyn-bridge-mcp/master/install.sh | bash
set -euo pipefail

repository="thomas-fazzari/roslyn-bridge-mcp"
install_directory="${ROSLYN_BRIDGE_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)/$(uname -m)" in
  Darwin/arm64) runtime=osx-arm64 ;;
  Darwin/x86_64) runtime=osx-x64 ;;
  Linux/x86_64) runtime=linux-x64 ;;
  Linux/aarch64 | Linux/arm64) runtime=linux-arm64 ;;
  *)
    printf 'Unsupported platform: %s/%s. On Windows, use install.ps1.\n' "$(uname -s)" "$(uname -m)" >&2
    exit 1
    ;;
esac

asset="roslyn-bridge-mcp-$runtime"
download="https://github.com/$repository/releases/latest/download"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT

printf 'Downloading %s...\n' "$asset"
curl -fsSL "$download/$asset" -o "$temporary_directory/$asset"
curl -fsSL "$download/SHA256SUMS" -o "$temporary_directory/SHA256SUMS"
expected="$(awk -v asset="$asset" '$2 == asset { print $1 }' "$temporary_directory/SHA256SUMS")"
if command -v sha256sum >/dev/null; then
  actual="$(sha256sum "$temporary_directory/$asset" | awk '{ print $1 }')"
else
  actual="$(shasum -a 256 "$temporary_directory/$asset" | awk '{ print $1 }')"
fi
if [[ -z "$expected" || "$actual" != "$expected" ]]; then
  printf 'Checksum verification failed for %s.\n' "$asset" >&2
  exit 1
fi

mkdir -p "$install_directory"
chmod +x "$temporary_directory/$asset"
mv -f "$temporary_directory/$asset" "$install_directory/roslyn-bridge-mcp"
printf 'Installed %s\n\n' "$install_directory/roslyn-bridge-mcp"

case ":$PATH:" in
  *":$install_directory:"*) ;;
  *) printf 'Add %s to your PATH to run roslyn-bridge-mcp from a shell.\n\n' "$install_directory" ;;
esac

"$install_directory/roslyn-bridge-mcp" setup
