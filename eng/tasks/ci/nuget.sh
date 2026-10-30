#!/usr/bin/env bash
#MISE description="Pushes the tool package to nuget.org"
set -euo pipefail

# nuget.org answers 409 to a reserved package ID as well as to a duplicate, so every refusal fails the job
dotnet nuget push artifacts/nupkg/*.nupkg --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
