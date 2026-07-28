[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$version = [string]$props.Project.PropertyGroup.Version
$tag = "v$version"
$repository = "https://github.com/project-base-mirror/tool-storage-browser"
$manifestPath = Join-Path $repositoryRoot "docs\site\update.json"
$sitePath = Join-Path $repositoryRoot "docs\site\index.html"

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1) {
    throw "docs/site/update.json schemaVersion must be 1."
}
if ([string]$manifest.tagName -cne $tag) {
    throw "Update manifest tagName $($manifest.tagName) does not match $tag."
}
if ([string]$manifest.version -cne $version) {
    throw "Update manifest version $($manifest.version) does not match $version."
}

$expectedRelease = "$repository/releases/tag/$tag"
$expectedDownload = "$repository/releases/download/$tag/S3Explorer-$tag-win-x64.zip"
if ([string]$manifest.releasePage -cne $expectedRelease) {
    throw "Update manifest releasePage does not match $expectedRelease."
}
if ([string]$manifest.downloadUrl -cne $expectedDownload) {
    throw "Update manifest downloadUrl does not match $expectedDownload."
}

$site = Get-Content -LiteralPath $sitePath -Raw
if (-not $site.Contains($expectedRelease, [StringComparison]::Ordinal)) {
    throw "Pages index does not link to $expectedRelease."
}
if (-not $site.Contains($expectedDownload, [StringComparison]::Ordinal)) {
    throw "Pages index does not link to $expectedDownload."
}

Write-Host "Update manifest verified for $tag."
