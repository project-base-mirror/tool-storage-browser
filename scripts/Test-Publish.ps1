[CmdletBinding()]
param(
    [switch]$SkipPackageBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishScript = Join-Path $PSScriptRoot "Publish.ps1"
$releaseRoot = Join-Path $repositoryRoot "artifacts\release"
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$version = [string]$props.Project.PropertyGroup.Version
$frameworkName = "S3Explorer-v$version-win-x64"
$selfContainedName = "S3Explorer-v$version-win-x64-self-contained"

& (Join-Path $PSScriptRoot "Test-UpdateManifest.ps1")
if (-not $?) {
    throw "Update manifest validation failed."
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

foreach ($relativePath in @("build.bat", "publish.bat", "cli.bat", "scripts\Build.ps1", "scripts\Publish.ps1")) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath)) -Message "Missing required script: $relativePath"
}

$publishSource = Get-Content -LiteralPath $publishScript -Raw
Assert-True -Condition ($publishSource -cnotmatch '\$OutputRoot\b') -Message "Publish.ps1 must not accept or use a movable OutputRoot."
Assert-True -Condition ($publishSource -match 'Join-Path \$repositoryRoot "artifacts"') -Message "Publish.ps1 must anchor output to the repository artifacts directory."
Assert-True -Condition ($publishSource -match 'Start-Process -FilePath "explorer.exe"') -Message "Publish.ps1 must open the actual output directory after success."
Assert-True -Condition ($publishSource -match 'Remove-Item -LiteralPath \$outputRoot -Recurse -Force') -Message "Publish.ps1 must rebuild the release directory from a clean state."
Assert-True -Condition ($publishSource -match 'Add-Type -AssemblyName System.IO.Compression.FileSystem') -Message "Publish.ps1 must load ZipFile support in Windows PowerShell."

foreach ($batchName in @("build.bat", "publish.bat")) {
    $batchSource = Get-Content -LiteralPath (Join-Path $repositoryRoot $batchName) -Raw
    Assert-True -Condition ($batchSource -match '%~dp0') -Message "$batchName must resolve paths from the repository root."
    Assert-True -Condition ($batchSource -match 'S3EXPLORER_NO_PAUSE') -Message "$batchName must support non-interactive validation."
    Assert-True -Condition ($batchSource -match '(?i)pause') -Message "$batchName must retain the window after failure."
}

if (-not $SkipPackageBuild) {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $staleArtifact = Join-Path $releaseRoot "stale-artifact.txt"
    Set-Content -LiteralPath $staleArtifact -Value "must be removed" -Encoding utf8

    & $publishScript -SkipValidation -NoOpen
    if (-not $?) {
        throw "Publish.ps1 failed."
    }

    Assert-True -Condition (-not (Test-Path -LiteralPath $staleArtifact)) -Message "Publish.ps1 left a stale file in artifacts\release."
}

$expectedPaths = @(
    $frameworkName,
    $selfContainedName,
    "$frameworkName.zip",
    "$selfContainedName.zip",
    "release-metrics.json"
)

foreach ($relativePath in $expectedPaths) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $releaseRoot $relativePath)) -Message "Missing release artifact: $relativePath"
}

if (-not $SkipPackageBuild) {
    $actualNames = @(Get-ChildItem -LiteralPath $releaseRoot | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedNames = @($expectedPaths | Sort-Object)
    Assert-True -Condition (($actualNames -join '|') -ceq ($expectedNames -join '|')) -Message "artifacts\release contains stale or unexpected top-level entries."

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($zipName in @("$frameworkName.zip", "$selfContainedName.zip")) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot $zipName))
        try {
            $entryNames = @($archive.Entries | Select-Object -ExpandProperty FullName)
            Assert-True -Condition ($entryNames -contains "S3Explorer.exe") -Message "$zipName does not contain S3Explorer.exe."
            Assert-True -Condition ($entryNames -contains "S3Explorer.dll") -Message "$zipName does not contain S3Explorer.dll."
            Assert-True -Condition ($entryNames -contains "s3explorer-cli.exe") -Message "$zipName does not contain s3explorer-cli.exe."
            Assert-True -Condition ($entryNames -contains "s3explorer-cli.dll") -Message "$zipName does not contain s3explorer-cli.dll."
        }
        finally {
            $archive.Dispose()
        }
    }
}

$metrics = Get-Content -LiteralPath (Join-Path $releaseRoot "release-metrics.json") -Raw | ConvertFrom-Json
Assert-True -Condition ($metrics.packages.Count -eq 2) -Message "release-metrics.json must contain exactly two packages."
Assert-True -Condition ($metrics.packages.name -contains $frameworkName) -Message "Framework-dependent package metric is missing."
Assert-True -Condition ($metrics.packages.name -contains $selfContainedName) -Message "Self-contained package metric is missing."

Write-Host "Publish script tests passed."
Write-Host "Verified release directory: $((Resolve-Path -LiteralPath $releaseRoot).Path)"
