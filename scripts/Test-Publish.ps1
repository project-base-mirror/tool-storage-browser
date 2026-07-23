[CmdletBinding()]
param(
    [switch]$SkipPackageBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishScript = Join-Path $PSScriptRoot "Publish.ps1"
$releaseRoot = Join-Path $repositoryRoot "artifacts\release"

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

foreach ($relativePath in @("build.bat", "publish.bat", "scripts\Build.ps1", "scripts\Publish.ps1")) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath)) -Message "Missing required script: $relativePath"
}

$publishSource = Get-Content -LiteralPath $publishScript -Raw
Assert-True -Condition ($publishSource -cnotmatch '\$OutputRoot\b') -Message "Publish.ps1 must not accept or use a movable OutputRoot."
Assert-True -Condition ($publishSource -match 'Join-Path \$repositoryRoot "artifacts"') -Message "Publish.ps1 must anchor output to the repository artifacts directory."
Assert-True -Condition ($publishSource -match 'Start-Process -FilePath "explorer.exe"') -Message "Publish.ps1 must open the actual output directory after success."

foreach ($batchName in @("build.bat", "publish.bat")) {
    $batchSource = Get-Content -LiteralPath (Join-Path $repositoryRoot $batchName) -Raw
    Assert-True -Condition ($batchSource -match '%~dp0') -Message "$batchName must resolve paths from the repository root."
    Assert-True -Condition ($batchSource -match 'S3EXPLORER_NO_PAUSE') -Message "$batchName must support non-interactive validation."
    Assert-True -Condition ($batchSource -match '(?i)pause') -Message "$batchName must retain the window after failure."
}

if (-not $SkipPackageBuild) {
    & $publishScript -SkipValidation -NoOpen
    if (-not $?) {
        throw "Publish.ps1 failed."
    }
}

$expectedPaths = @(
    "S3Explorer-win-x64",
    "S3Explorer-win-x64-self-contained",
    "S3Explorer-win-x64.zip",
    "S3Explorer-win-x64-self-contained.zip",
    "release-metrics.json"
)

foreach ($relativePath in $expectedPaths) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $releaseRoot $relativePath)) -Message "Missing release artifact: $relativePath"
}

$metrics = Get-Content -LiteralPath (Join-Path $releaseRoot "release-metrics.json") -Raw | ConvertFrom-Json
Assert-True -Condition ($metrics.packages.Count -eq 2) -Message "release-metrics.json must contain exactly two packages."
Assert-True -Condition ($metrics.packages.name -contains "S3Explorer-win-x64") -Message "Framework-dependent package metric is missing."
Assert-True -Condition ($metrics.packages.name -contains "S3Explorer-win-x64-self-contained") -Message "Self-contained package metric is missing."

Write-Host "Publish script tests passed."
Write-Host "Verified release directory: $((Resolve-Path -LiteralPath $releaseRoot).Path)"
