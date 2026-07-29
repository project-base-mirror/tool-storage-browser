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
$contractsName = "S3Explorer.Contracts-v$version"
$installerName = "S3Explorer-v$version-win-x64-setup.msi"

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

foreach ($relativePath in @("build.bat", "publish.bat", "cli.bat", "scripts\Build.ps1", "scripts\Publish.ps1", "scripts\Sign-Artifacts.ps1", "installer\S3Explorer.Installer.wixproj", "installer\Package.wxs")) {
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
    $contractsName,
    "$contractsName.zip",
    $installerName,
    "release-metrics.json"
)

foreach ($relativePath in $expectedPaths) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $releaseRoot $relativePath)) -Message "Missing release artifact: $relativePath"
}

$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$msiPath = (Resolve-Path -LiteralPath (Join-Path $releaseRoot $installerName)).Path
$msiDatabase = $windowsInstaller.GetType().InvokeMember(
    "OpenDatabase", "InvokeMethod", $null, $windowsInstaller, @($msiPath, 0))
function Get-MsiQueryValue {
    param([Parameter(Mandatory)][string]$Query)

    $view = $msiDatabase.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $msiDatabase, @($Query))
    $null = $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) { return $null }
    return $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1)
}

$msiVersion = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
$msiProductName = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'"
Assert-True -Condition ($msiVersion -ceq $version) -Message "MSI ProductVersion $msiVersion does not match $version."
Assert-True -Condition ($msiProductName -ceq "S3 Explorer") -Message "MSI ProductName is invalid: $msiProductName"
$guiFile = Get-MsiQueryValue -Query "SELECT ``FileName`` FROM ``File`` WHERE ``File``='S3ExplorerExe'"
$cliFile = Get-MsiQueryValue -Query "SELECT ``FileName`` FROM ``File`` WHERE ``File``='S3ExplorerCliExe'"
Assert-True -Condition ($guiFile -match '(?i)S3Explorer\.exe$') -Message "MSI does not contain S3Explorer.exe."
Assert-True -Condition ($cliFile -match '(?i)s3explorer-cli\.exe$') -Message "MSI does not contain s3explorer-cli.exe."

if (-not $SkipPackageBuild) {
    $actualNames = @(Get-ChildItem -LiteralPath $releaseRoot | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedNames = @($expectedPaths | Sort-Object)
    Assert-True -Condition (($actualNames -join '|') -ceq ($expectedNames -join '|')) -Message "artifacts\release contains stale or unexpected top-level entries."

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($zipName in @("$frameworkName.zip", "$selfContainedName.zip")) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot $zipName))
        try {
            $entryNames = @($archive.Entries | Select-Object -ExpandProperty FullName)
            $expectedEntries = @("S3Explorer.exe", "s3explorer-cli.exe") | Sort-Object
            $actualEntries = @($entryNames | Sort-Object)
            Assert-True -Condition (($actualEntries -join '|') -ceq ($expectedEntries -join '|')) -Message "$zipName must contain only the desktop and CLI single-file executables; found: $($actualEntries -join ', ')."
        }
        finally {
            $archive.Dispose()
        }
    }

    $contractsZipName = "$contractsName.zip"
    $contractsArchive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $releaseRoot $contractsZipName))
    try {
        $actualEntries = @($contractsArchive.Entries | Select-Object -ExpandProperty FullName | Sort-Object)
        $expectedEntries = @("README.md", "S3Explorer.Contracts.dll", "S3Explorer.Contracts.xml") | Sort-Object
        Assert-True -Condition (($actualEntries -join '|') -ceq ($expectedEntries -join '|')) -Message "$contractsZipName contains unexpected entries: $($actualEntries -join ', ')."
    }
    finally {
        $contractsArchive.Dispose()
    }
}

$contractsDirectory = Join-Path $releaseRoot $contractsName
$contractsAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $contractsDirectory "S3Explorer.Contracts.dll"))
Assert-True -Condition ($contractsAssembly.Version.ToString() -ceq "$version.0") -Message "Contracts assembly version $($contractsAssembly.Version) does not match $version.0."
foreach ($contractsFile in @("S3Explorer.Contracts.dll", "S3Explorer.Contracts.xml", "README.md")) {
    $item = Get-Item -LiteralPath (Join-Path $contractsDirectory $contractsFile)
    Assert-True -Condition ($item.Length -gt 0) -Message "Unity contracts package file is empty: $contractsFile"
}

$metrics = Get-Content -LiteralPath (Join-Path $releaseRoot "release-metrics.json") -Raw | ConvertFrom-Json
Assert-True -Condition ($metrics.packages.Count -eq 2) -Message "release-metrics.json must contain exactly two packages."
Assert-True -Condition ([bool]$metrics.singleFileEnabled) -Message "release-metrics.json must report single-file publishing."
Assert-True -Condition ($metrics.packages.name -contains $frameworkName) -Message "Framework-dependent package metric is missing."
Assert-True -Condition ($metrics.packages.name -contains $selfContainedName) -Message "Self-contained package metric is missing."
Assert-True -Condition ($metrics.contracts.name -ceq "$contractsName.zip") -Message "Unity contracts package metric is missing."
Assert-True -Condition ($metrics.installer.name -ceq $installerName) -Message "Installer metric is missing."

Write-Host "Publish script tests passed."
Write-Host "Verified release directory: $((Resolve-Path -LiteralPath $releaseRoot).Path)"
