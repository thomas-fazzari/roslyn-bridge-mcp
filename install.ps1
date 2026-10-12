# Installs the latest RoslynBridge release, then runs its setup.
# Usage: irm https://raw.githubusercontent.com/thomas-fazzari/roslyn-bridge-mcp/master/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$repository = 'thomas-fazzari/roslyn-bridge-mcp'
$installDirectory = if ($env:ROSLYN_BRIDGE_INSTALL_DIR) { $env:ROSLYN_BRIDGE_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\roslyn-bridge-mcp' }
$asset = 'roslyn-bridge-mcp-win-x64.exe'
$download = "https://github.com/$repository/releases/latest/download"
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid())
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    Write-Host "Downloading $asset..."
    Invoke-WebRequest "$download/$asset" -OutFile (Join-Path $temporaryDirectory $asset)
    Invoke-WebRequest "$download/SHA256SUMS" -OutFile (Join-Path $temporaryDirectory 'SHA256SUMS')
    $expected = (Get-Content (Join-Path $temporaryDirectory 'SHA256SUMS') | Where-Object { $_ -match " $([regex]::Escape($asset))$" }) -replace ' .*', ''
    $actual = (Get-FileHash (Join-Path $temporaryDirectory $asset) -Algorithm SHA256).Hash
    if (-not $expected -or $actual -ne $expected) { throw "Checksum verification failed for $asset." }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    $executable = Join-Path $installDirectory 'roslyn-bridge-mcp.exe'
    Move-Item (Join-Path $temporaryDirectory $asset) $executable -Force
    Write-Host "Installed $executable`n"

    if (($env:PATH -split ';') -notcontains $installDirectory) {
        Write-Host "Add $installDirectory to your PATH to run roslyn-bridge-mcp from a shell.`n"
    }

    & $executable setup
}
finally {
    Remove-Item $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
