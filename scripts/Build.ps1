[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$NoOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $repositoryRoot "S3Explorer.sln"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$buildRoot = Join-Path $artifactsRoot "build"

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found on PATH. Install the .NET 10 SDK first."
}

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
Invoke-DotNet -Arguments @(
    "restore",
    $solution,
    "--artifacts-path", $buildRoot
)
Invoke-DotNet -Arguments @(
    "build",
    $solution,
    "-c", $Configuration,
    "--no-restore",
    "--artifacts-path", $buildRoot
)

$resolvedBuildRoot = (Resolve-Path -LiteralPath $buildRoot).Path
Write-Host ""
Write-Host "Build artifacts: $resolvedBuildRoot"

if (-not $NoOpen) {
    Start-Process -FilePath "explorer.exe" -ArgumentList @($resolvedBuildRoot) | Out-Null
}
