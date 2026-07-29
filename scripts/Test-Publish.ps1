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
$frameworkInstallerName = "S3Explorer-v$version-win-x64-framework-dependent-setup.msi"

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

foreach ($relativePath in @("build.bat", "publish.bat", "cli.bat", "scripts\Build.ps1", "scripts\Publish.ps1", "scripts\Sign-Artifacts.ps1", "scripts\Verify-RemoteRelease.ps1", "installer\S3Explorer.Installer.wixproj", "installer\Package.wxs")) {
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath)) -Message "Missing required script: $relativePath"
}

$publishSource = Get-Content -LiteralPath $publishScript -Raw
Assert-True -Condition ($publishSource -cnotmatch '\$OutputRoot\b') -Message "Publish.ps1 must not accept or use a movable OutputRoot."
Assert-True -Condition ($publishSource -match 'Join-Path \$repositoryRoot "artifacts"') -Message "Publish.ps1 must anchor output to the repository artifacts directory."
Assert-True -Condition ($publishSource -match 'Start-Process -FilePath "explorer.exe"') -Message "Publish.ps1 must open the actual output directory after success."
Assert-True -Condition ($publishSource -match 'Remove-Item -LiteralPath \$outputRoot -Recurse -Force') -Message "Publish.ps1 must rebuild the release directory from a clean state."
Assert-True -Condition ($publishSource -match 'Add-Type -AssemblyName System.IO.Compression.FileSystem') -Message "Publish.ps1 must load ZipFile support in Windows PowerShell."

$installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "installer\Package.wxs") -Raw
Assert-True -Condition ($installerSource -cnotmatch 'Lorem ipsum|LicenseAgreementDlg') -Message "Installer must not expose placeholder license content."
Assert-True -Condition ($installerSource -match 'ProgramFiles64Folder') -Message "Installer must default to the 64-bit Program Files directory."
$applicationIconPath = Join-Path $repositoryRoot "src\S3Explorer.App\Assets\S3Explorer.ico"
Assert-True -Condition (Test-Path -LiteralPath $applicationIconPath -PathType Leaf) -Message "Application icon is missing."

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
    $frameworkInstallerName,
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

function Get-MsiQueryRowCount {
    param([Parameter(Mandatory)][string]$Query)

    $view = $msiDatabase.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $msiDatabase, @($Query))
    $null = $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
    $count = 0
    while ($null -ne $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)) {
        $count++
    }
    return $count
}

function Get-MsiQueryValues {
    param([Parameter(Mandatory)][string]$Query)

    $view = $msiDatabase.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $msiDatabase, @($Query))
    $null = $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
    $values = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
        if ($null -eq $record) { break }
        $values.Add([string]$record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1))
    }
    return $values.ToArray()
}

$msiVersion = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
$msiProductName = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'"
Assert-True -Condition ($msiVersion -ceq $version) -Message "MSI ProductVersion $msiVersion does not match $version."
Assert-True -Condition ($msiProductName -ceq "S3 Explorer") -Message "MSI ProductName is invalid: $msiProductName"
$guiFile = Get-MsiQueryValue -Query "SELECT ``FileName`` FROM ``File`` WHERE ``File``='S3ExplorerExe'"
$cliFile = Get-MsiQueryValue -Query "SELECT ``FileName`` FROM ``File`` WHERE ``File``='S3ExplorerCliExe'"
Assert-True -Condition ($guiFile -match '(?i)S3Explorer\.exe$') -Message "MSI does not contain S3Explorer.exe."
Assert-True -Condition ($cliFile -match '(?i)s3explorer-cli\.exe$') -Message "MSI does not contain s3explorer-cli.exe."
$msiLogging = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='MsiLogging'"
$applicationFolder = Get-MsiQueryValue -Query "SELECT ``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='APPLICATIONFOLDER'"
$applicationFolderParent = Get-MsiQueryValue -Query "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='APPLICATIONFOLDER'"
$installDialog = Get-MsiQueryValue -Query "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog``='S3ExplorerInstallDirDlg'"
$licenseDialog = Get-MsiQueryValue -Query "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog``='LicenseAgreementDlg'"
$desktopFeature = Get-MsiQueryValue -Query "SELECT ``Feature`` FROM ``Feature`` WHERE ``Feature``='DesktopShortcutFeature'"
$desktopShortcut = Get-MsiQueryValue -Query "SELECT ``Shortcut`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'"
$desktopShortcutProperty = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLDESKTOPSHORTCUT'"
$desktopShortcutCondition = Get-MsiQueryValue -Query "SELECT ``Condition`` FROM ``Component`` WHERE ``Component``='DesktopShortcutComponent'"
$arpIcon = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPPRODUCTICON'"
$installerIcon = Get-MsiQueryValue -Query "SELECT ``Name`` FROM ``Icon`` WHERE ``Name``='S3ExplorerIcon.exe'"
$startMenuIcon = Get-MsiQueryValue -Query "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'"
$desktopIcon = Get-MsiQueryValue -Query "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'"
$installerFlavor = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLERFLAVOR'"
$selfContainedFileCount = Get-MsiQueryRowCount -Query "SELECT ``File`` FROM ``File``"
$selfContainedFileNames = @(Get-MsiQueryValues -Query "SELECT ``FileName`` FROM ``File``")
$selfContainedManagedAssembly = @($selfContainedFileNames | Where-Object { $_ -match '(?i)S3Explorer\.dll$' })
$selfContainedRuntime = @($selfContainedFileNames | Where-Object { $_ -match '(?i)coreclr\.dll$' })
Assert-True -Condition ($msiLogging -ceq "voicewarmup") -Message "MSI automatic logging is not enabled."
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($applicationFolder)) -Message "MSI does not expose an application install directory."
Assert-True -Condition ($applicationFolderParent -ceq "ProgramFiles64Folder") -Message "MSI application directory is not rooted under ProgramFiles64Folder."
Assert-True -Condition ($installDialog -ceq "S3ExplorerInstallDirDlg") -Message "MSI does not contain the install-directory dialog."
Assert-True -Condition ([string]::IsNullOrWhiteSpace($licenseDialog)) -Message "MSI still contains a placeholder license dialog."
Assert-True -Condition ($desktopFeature -ceq "DesktopShortcutFeature") -Message "MSI does not expose the optional desktop shortcut feature."
Assert-True -Condition ($desktopShortcut -ceq "DesktopShortcut") -Message "MSI does not contain the desktop shortcut."
Assert-True -Condition ($desktopShortcutProperty -ceq "1") -Message "MSI does not select the desktop shortcut by default."
Assert-True -Condition ($desktopShortcutCondition -ceq "INSTALLDESKTOPSHORTCUT = 1") -Message "MSI desktop shortcut is not controlled by the setup checkbox."
Assert-True -Condition ($arpIcon -ceq "S3ExplorerIcon.exe") -Message "MSI does not expose the application icon in Programs and Features."
Assert-True -Condition ($installerIcon -ceq "S3ExplorerIcon.exe") -Message "MSI application icon resource is missing."
Assert-True -Condition ($startMenuIcon -ceq "S3ExplorerIcon.exe") -Message "Start menu shortcut does not use the application icon."
Assert-True -Condition ($desktopIcon -ceq "S3ExplorerIcon.exe") -Message "Desktop shortcut does not use the application icon."
Assert-True -Condition ($installerFlavor -ceq "self-contained") -Message "Primary MSI is not marked self-contained."
Assert-True -Condition ($selfContainedFileCount -gt 2) -Message "Primary MSI still contains only bundled single-file executables."
Assert-True -Condition ($selfContainedManagedAssembly.Count -gt 0) -Message "Primary MSI does not contain the unpacked application assemblies."
Assert-True -Condition ($selfContainedRuntime.Count -gt 0) -Message "Primary MSI does not contain the self-contained .NET runtime."

$frameworkMsiPath = (Resolve-Path -LiteralPath (Join-Path $releaseRoot $frameworkInstallerName)).Path
$msiDatabase = $windowsInstaller.GetType().InvokeMember(
    "OpenDatabase", "InvokeMethod", $null, $windowsInstaller, @($frameworkMsiPath, 0))
$frameworkVersion = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
$frameworkFlavor = Get-MsiQueryValue -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLERFLAVOR'"
$frameworkFileCount = Get-MsiQueryRowCount -Query "SELECT ``File`` FROM ``File``"
$frameworkFileNames = @(Get-MsiQueryValues -Query "SELECT ``FileName`` FROM ``File``")
$frameworkManagedAssembly = @($frameworkFileNames | Where-Object { $_ -match '(?i)S3Explorer\.dll$' })
$frameworkRuntime = @($frameworkFileNames | Where-Object { $_ -match '(?i)coreclr\.dll$' })
$frameworkRuntimeConfig = @($frameworkFileNames | Where-Object { $_ -match '(?i)S3Explorer\.runtimeconfig\.json$' })
Assert-True -Condition ($frameworkVersion -ceq $version) -Message "Framework-dependent MSI ProductVersion $frameworkVersion does not match $version."
Assert-True -Condition ($frameworkFlavor -ceq "framework-dependent") -Message "Additional MSI is not marked framework-dependent."
Assert-True -Condition ($frameworkFileCount -gt 2) -Message "Framework-dependent MSI still contains only bundled single-file executables."
Assert-True -Condition ($frameworkManagedAssembly.Count -gt 0) -Message "Framework-dependent MSI does not contain the unpacked application assemblies."
Assert-True -Condition ($frameworkRuntimeConfig.Count -gt 0) -Message "Framework-dependent MSI does not contain the runtime configuration."
Assert-True -Condition ($frameworkRuntime.Count -eq 0) -Message "Framework-dependent MSI unexpectedly embeds the .NET runtime."

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
Assert-True -Condition ($contractsAssembly.Version.ToString() -ceq "1.0.0.0") -Message "Contracts assembly ABI version $($contractsAssembly.Version) does not match stable version 1.0.0.0."
$contractsFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($contractsDll).FileVersion
Assert-True -Condition ($contractsFileVersion -ceq "$version.0") -Message "Contracts file version $contractsFileVersion does not match product version $version.0."
foreach ($contractsFile in @("S3Explorer.Contracts.dll", "S3Explorer.Contracts.xml", "README.md")) {
    $item = Get-Item -LiteralPath (Join-Path $contractsDirectory $contractsFile)
    Assert-True -Condition ($item.Length -gt 0) -Message "Unity contracts package file is empty: $contractsFile"
}

$metrics = Get-Content -LiteralPath (Join-Path $releaseRoot "release-metrics.json") -Raw | ConvertFrom-Json
Assert-True -Condition ($metrics.packages.Count -eq 2) -Message "release-metrics.json must contain exactly two packages."
Assert-True -Condition ([bool]$metrics.singleFileEnabled) -Message "release-metrics.json must report single-file publishing."
Assert-True -Condition (-not [bool]$metrics.installerSingleFileEnabled) -Message "release-metrics.json must report multi-file installer publishing."
Assert-True -Condition ($metrics.packages.name -contains $frameworkName) -Message "Framework-dependent package metric is missing."
Assert-True -Condition ($metrics.packages.name -contains $selfContainedName) -Message "Self-contained package metric is missing."
Assert-True -Condition ($metrics.contracts.name -ceq "$contractsName.zip") -Message "Unity contracts package metric is missing."
Assert-True -Condition ($metrics.installer.name -ceq $installerName) -Message "Installer metric is missing."
Assert-True -Condition ($metrics.frameworkInstaller.name -ceq $frameworkInstallerName) -Message "Framework-dependent installer metric is missing."
Assert-True -Condition ($metrics.installerPayloads.Count -eq 2) -Message "Installer payload metrics must contain both deployment modes."
Assert-True -Condition (($metrics.installerPayloads | Where-Object name -ceq 'self-contained').fileCount -gt 2) -Message "Self-contained installer payload metric is invalid."
Assert-True -Condition (($metrics.installerPayloads | Where-Object name -ceq 'framework-dependent').fileCount -gt 2) -Message "Framework-dependent installer payload metric is invalid."

Write-Host "Publish script tests passed."
Write-Host "Verified release directory: $((Resolve-Path -LiteralPath $releaseRoot).Path)"
