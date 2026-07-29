[CmdletBinding()]
param(
    [string]$Tag,
    [string]$Repository = "project-base-mirror/tool-storage-browser",
    [switch]$FullDownload,
    [switch]$RequireSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$projectVersion = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$projectVersion" }
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Tag must use vX.Y.Z: $Tag" }
$version = $Matches[1]

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) was not found on PATH."
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $value = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
    return ($value | Out-String) | ConvertFrom-Json
}

function Download-Asset {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Destination
    )
    & gh release download $Tag --repo $Repository --pattern $Name --dir $Destination
    if ($LASTEXITCODE -ne 0) { throw "Failed to download release asset: $Name" }
    $path = Join-Path $Destination $Name
    Assert-True -Condition (Test-Path -LiteralPath $path -PathType Leaf) -Message "Downloaded asset is missing: $Name"
    return $path
}

function Get-ZipEntries {
    param([Parameter(Mandatory)][string]$Path)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object) }
    finally { $archive.Dispose() }
}

function Assert-FileHash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True -Condition ($actual -ceq $Expected) -Message "SHA-256 mismatch for $(Split-Path -Leaf $Path): expected $Expected, actual $actual"
}

$frameworkName = "S3Explorer-$Tag-win-x64.zip"
$selfContainedName = "S3Explorer-$Tag-win-x64-self-contained.zip"
$contractsName = "S3Explorer.Contracts-$Tag.zip"
$installerName = "S3Explorer-$Tag-win-x64-setup.msi"
$frameworkInstallerName = "S3Explorer-$Tag-win-x64-framework-dependent-setup.msi"
$expectedNames = @(
    $frameworkName,
    $selfContainedName,
    $contractsName,
    $installerName,
    $frameworkInstallerName,
    "release-metrics.json",
    "SHA256SUMS.txt"
) | Sort-Object

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "s3explorer-release-$Tag-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $release = Invoke-GhJson -Arguments @(
        "release", "view", $Tag, "--repo", $Repository,
        "--json", "tagName,isDraft,isPrerelease,assets,url")
    Assert-True -Condition ([string]$release.tagName -ceq $Tag) -Message "Release tag mismatch."
    Assert-True -Condition (-not [bool]$release.isDraft) -Message "Release is still a draft."
    Assert-True -Condition (-not [bool]$release.isPrerelease) -Message "Release is marked as prerelease."
    $latest = Invoke-GhJson -Arguments @("release", "view", "--repo", $Repository, "--json", "tagName")
    Assert-True -Condition ([string]$latest.tagName -ceq $Tag) -Message "Latest Release is $($latest.tagName), expected $Tag."

    $actualNames = @($release.assets | ForEach-Object { [string]$_.name } | Sort-Object)
    Assert-True -Condition (($actualNames -join '|') -ceq ($expectedNames -join '|')) -Message (
        "Release assets differ. Expected: $($expectedNames -join ', '); actual: $($actualNames -join ', ')")
    foreach ($asset in $release.assets) {
        Assert-True -Condition ([string]$asset.state -ceq "uploaded") -Message "Asset is not uploaded: $($asset.name)"
        Assert-True -Condition ([int64]$asset.size -gt 0) -Message "Asset is empty: $($asset.name)"
    }

    $checksumPath = Download-Asset -Name "SHA256SUMS.txt" -Destination $temporaryRoot
    $checksums = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^([0-9a-f]{64}) \*(.+)$') { throw "Invalid checksum line: $line" }
        $checksums[$Matches[2]] = $Matches[1]
    }
    foreach ($packageName in @($frameworkName, $selfContainedName, $contractsName, $installerName, $frameworkInstallerName)) {
        Assert-True -Condition $checksums.ContainsKey($packageName) -Message "Checksum is missing for $packageName."
        $asset = $release.assets | Where-Object { $_.name -ceq $packageName } | Select-Object -First 1
        $digest = [string]$asset.digest
        if ($digest -notmatch '^sha256:([0-9a-f]{64})$') {
            throw "GitHub did not provide a SHA-256 digest for $packageName. Re-run with -FullDownload for byte-level verification."
        }
        Assert-True -Condition ($Matches[1] -ceq [string]$checksums[$packageName]) -Message (
            "GitHub digest does not match SHA256SUMS.txt for $packageName.")
    }

    $metricsPath = Download-Asset -Name "release-metrics.json" -Destination $temporaryRoot
    $frameworkPath = Download-Asset -Name $frameworkName -Destination $temporaryRoot
    $contractsPath = Download-Asset -Name $contractsName -Destination $temporaryRoot
    $frameworkInstallerPath = Download-Asset -Name $frameworkInstallerName -Destination $temporaryRoot
    Assert-FileHash -Path $frameworkPath -Expected ([string]$checksums[$frameworkName])
    Assert-FileHash -Path $contractsPath -Expected ([string]$checksums[$contractsName])
    Assert-FileHash -Path $frameworkInstallerPath -Expected ([string]$checksums[$frameworkInstallerName])

    $frameworkEntries = Get-ZipEntries -Path $frameworkPath
    $expectedApplicationEntries = @("S3Explorer.exe", "s3explorer-cli.exe") | Sort-Object
    Assert-True -Condition (($frameworkEntries -join '|') -ceq ($expectedApplicationEntries -join '|')) -Message (
        "$frameworkName contains unexpected entries: $($frameworkEntries -join ', ')")
    $contractsEntries = Get-ZipEntries -Path $contractsPath
    Assert-True -Condition (($contractsEntries -join '|') -ceq "README.md|S3Explorer.Contracts.dll|S3Explorer.Contracts.xml") -Message (
        "$contractsName contains unexpected entries: $($contractsEntries -join ', ')")

    $frameworkDirectory = Join-Path $temporaryRoot "framework"
    $contractsDirectory = Join-Path $temporaryRoot "contracts"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($frameworkPath, $frameworkDirectory)
    [System.IO.Compression.ZipFile]::ExtractToDirectory($contractsPath, $contractsDirectory)
    $versionResult = & (Join-Path $frameworkDirectory "s3explorer-cli.exe") version --output json
    if ($LASTEXITCODE -ne 0) { throw "Downloaded CLI version smoke failed with exit code $LASTEXITCODE." }
    $versionJson = ($versionResult | Out-String) | ConvertFrom-Json
    Assert-True -Condition ([bool]$versionJson.ok) -Message "Downloaded CLI version smoke returned ok=false."
    Assert-True -Condition ([string]$versionJson.data.version -ceq $version) -Message "Downloaded CLI version mismatch."
    $contractsAssembly = [Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $contractsDirectory "S3Explorer.Contracts.dll"))
    Assert-True -Condition ($contractsAssembly.Version.ToString() -ceq "$version.0") -Message "Contracts assembly version mismatch."

    $metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
    Assert-True -Condition ($metrics.packages.Count -eq 2) -Message "release-metrics.json package count is invalid."
    Assert-True -Condition ([string]$metrics.contracts.name -ceq $contractsName) -Message "Contracts metric name is invalid."
    Assert-True -Condition ([string]$metrics.installer.name -ceq $installerName) -Message "Installer metric name is invalid."
    Assert-True -Condition ([string]$metrics.frameworkInstaller.name -ceq $frameworkInstallerName) -Message "Framework-dependent installer metric name is invalid."
    Assert-True -Condition (-not [bool]$metrics.installerSingleFileEnabled) -Message "Installer payloads must be reported as multi-file."

    if ($FullDownload) {
        $selfContainedPath = Download-Asset -Name $selfContainedName -Destination $temporaryRoot
        $installerPath = Download-Asset -Name $installerName -Destination $temporaryRoot
        Assert-FileHash -Path $selfContainedPath -Expected ([string]$checksums[$selfContainedName])
        Assert-FileHash -Path $installerPath -Expected ([string]$checksums[$installerName])
        $selfContainedEntries = Get-ZipEntries -Path $selfContainedPath
        Assert-True -Condition (($selfContainedEntries -join '|') -ceq ($expectedApplicationEntries -join '|')) -Message (
            "$selfContainedName contains unexpected entries: $($selfContainedEntries -join ', ')")

        if ($RequireSigning) {
            $selfContainedDirectory = Join-Path $temporaryRoot "self-contained"
            [System.IO.Compression.ZipFile]::ExtractToDirectory($selfContainedPath, $selfContainedDirectory)
            foreach ($signedPath in @(
                (Join-Path $frameworkDirectory "S3Explorer.exe"),
                (Join-Path $frameworkDirectory "s3explorer-cli.exe"),
                (Join-Path $selfContainedDirectory "S3Explorer.exe"),
                (Join-Path $selfContainedDirectory "s3explorer-cli.exe"),
                $installerPath,
                $frameworkInstallerPath)) {
                $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
                Assert-True -Condition ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) -Message (
                    "Authenticode signature is not valid: $(Split-Path -Leaf $signedPath) ($($signature.Status))")
            }
        }
    }
    elseif ($RequireSigning) {
        throw "-RequireSigning requires -FullDownload because EXE and MSI signatures must be inspected locally."
    }

    $siteBase = "https://project-base-mirror.github.io/tool-storage-browser"
    $manifestResponse = Invoke-WebRequest -Uri "$siteBase/update.json" -UseBasicParsing
    Assert-True -Condition ($manifestResponse.StatusCode -eq 200) -Message "Pages update.json did not return HTTP 200."
    $onlineManifest = $manifestResponse.Content | ConvertFrom-Json
    Assert-True -Condition ([string]$onlineManifest.tagName -ceq $Tag) -Message "Pages update.json tag mismatch."
    Assert-True -Condition ([string]$onlineManifest.version -ceq $version) -Message "Pages update.json version mismatch."
    foreach ($url in @(
        "$siteBase/",
        "$siteBase/robots.txt",
        "$siteBase/sitemap.xml",
        "$siteBase/assets/social-card.png")) {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing
        Assert-True -Condition ($response.StatusCode -eq 200) -Message "$url did not return HTTP 200."
    }

    Write-Host "Remote release verified: $Tag"
    Write-Host "Mode: $(if ($FullDownload) { 'full byte download' } else { 'digest-first (large assets not downloaded)' })"
    Write-Host "Release: $($release.url)"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
