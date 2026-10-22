#!/usr/bin/env bash
#MISE description="Pushes the tool packages to nuget.org, the runtime packages before the tool package"
set -euo pipefail

id="$(dotnet msbuild src/RoslynBridge/RoslynBridge.csproj -getProperty:PackageId)"
cd artifacts/nupkg
tool="$id.$APP_VERSION.nupkg"

# nuget.org answers 409 to a reserved package ID as well as to a duplicate, so every refusal fails the job
push() {
  dotnet nuget push "$1" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
}

# The tool package points to the runtime packages, so it goes last
for package in "$id".*.nupkg; do
  if [[ "$package" != "$tool" ]]; then
    push "$package"
  fi
done
push "$tool"
