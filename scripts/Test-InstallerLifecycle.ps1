[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CurrentMsi,
    [string]$PreviousMsi = "",
    [string]$RollbackMsi = "",
    [string]$InstallDirectory = "",
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows -or -not [Environment]::Is64BitProcess) {
    throw "Installer lifecycle tests require 64-bit PowerShell on an isolated Windows machine."
}
if ($RollbackMsi -and -not $PreviousMsi) { throw "Rollback testing requires PreviousMsi." }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$CurrentMsi = (Resolve-Path -LiteralPath $CurrentMsi).Path
if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
    $PreviousMsi = (Resolve-Path -LiteralPath $PreviousMsi).Path
}
if ($RollbackMsi) { $RollbackMsi = (Resolve-Path -LiteralPath $RollbackMsi).Path }
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:ProgramFiles "S3 Explorer Lifecycle Test"
}
if (-not [IO.Path]::IsPathFullyQualified($InstallDirectory) -or $InstallDirectory.Contains('"')) {
    throw "InstallDirectory must be a safe absolute path."
}
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$runRoot = Join-Path $repositoryRoot "artifacts\installer-lifecycle\$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$startMenuShortcutPath = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\S3 Explorer\S3 Explorer.lnk"

function Invoke-Msi {
    param(
        [Parameter(Mandatory)][ValidateSet("Install", "Uninstall")][string]$Action,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$LogPath,
        [switch]$ReuseRegisteredDirectory,
        [int[]]$ExpectedExitCodes = @(0, 3010)
    )
    $verb = if ($Action -eq "Install") { "/i" } else { "/x" }
    $arguments = @($verb, "`"$Path`"", "/qn", "/norestart", "/l*v", "`"$LogPath`"")
    if ($Action -eq "Install") {
        if (-not $ReuseRegisteredDirectory) {
            $arguments += "APPLICATIONFOLDER=`"$InstallDirectory`""
        }
        $arguments += "INSTALLDESKTOPSHORTCUT=0"
    }
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
        -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -notin $ExpectedExitCodes) {
        throw "$Action returned MSI exit code $exitCode; expected $($ExpectedExitCodes -join '/'). Log: $LogPath"
    }
    return $exitCode
}

# Read shortcuts through the Unicode Shell interface. WScript.Shell's legacy
# TargetPath accessor can replace characters outside the Windows ANSI code page.
# https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw
if (-not ("S3Explorer.Lifecycle.ShortcutInspector" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace S3Explorer.Lifecycle
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    public sealed class ShortcutInfo
    {
        public string TargetPath { get; set; }
        public string WorkingDirectory { get; set; }
        public string IconLocation { get; set; }
    }

    public static class ShortcutInspector
    {
        public static ShortcutInfo Read(string path)
        {
            var instance = new ShellLink();
            try
            {
                ((IPersistFile)instance).Load(path, 0); // STGM_READ; never resolve or modify.
                var link = (IShellLinkW)instance;
                var target = new StringBuilder(32768);
                var workingDirectory = new StringBuilder(32768);
                var icon = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
                link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
                link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
                return new ShortcutInfo { TargetPath = target.ToString(), WorkingDirectory = workingDirectory.ToString(), IconLocation = icon.ToString() };
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }
    }
}
'@
}

function Test-InstalledApplication {
    param(
        [switch]$RequireUpdater,
        [switch]$RequireStableStartMenuShortcut
    )

    $gui = Join-Path $InstallDirectory "S3Explorer.exe"
    $cli = Join-Path $InstallDirectory "s3explorer-cli.exe"
    $updater = Join-Path $InstallDirectory "S3Explorer.Updater.exe"
    if (-not (Test-Path -LiteralPath $gui) -or -not (Test-Path -LiteralPath $cli)) {
        throw "Installed GUI or CLI is missing from $InstallDirectory."
    }
    if ($RequireUpdater -and -not (Test-Path -LiteralPath $updater)) {
        throw "Installed maintenance updater is missing from $InstallDirectory."
    }

    if ($RequireStableStartMenuShortcut) {
        if (-not (Test-Path -LiteralPath $startMenuShortcutPath -PathType Leaf)) {
            throw "Installed start menu shortcut is missing: $startMenuShortcutPath"
        }

        $shortcutEvidence = Join-Path $runRoot "shortcut-$([Guid]::NewGuid().ToString('N'))"
        Copy-Item -LiteralPath $startMenuShortcutPath -Destination "$shortcutEvidence.lnk"
        $shortcut = [S3Explorer.Lifecycle.ShortcutInspector]::Read($startMenuShortcutPath)
        $shortcut | ConvertTo-Json | Set-Content -LiteralPath "$shortcutEvidence.json" -Encoding utf8
        $expectedTarget = [IO.Path]::GetFullPath($gui)
        $actualTarget = [IO.Path]::GetFullPath([string]$shortcut.TargetPath)
        if ($actualTarget -ine $expectedTarget) {
            throw "Start menu shortcut target '$actualTarget' does not match '$expectedTarget'."
        }
        if ([IO.Path]::GetFullPath([string]$shortcut.WorkingDirectory).TrimEnd('\') -ine $InstallDirectory) {
            throw "Start menu shortcut working directory does not match the custom directory."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$shortcut.IconLocation) -and
            [string]$shortcut.IconLocation -match '(?i)\\Windows\\Installer\\') {
            throw "Start menu shortcut still depends on the Windows Installer icon cache: $($shortcut.IconLocation)"
        }
    }

    $shortcutMarker = Get-ItemPropertyValue -LiteralPath $userRegistryPath -Name "StartMenuShortcut" -ErrorAction Stop
    if ($shortcutMarker -ne 1) { throw "Start menu shortcut marker was not restored for the invoking user." }

    $versionOutput = & $cli version --output json --non-interactive 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Installed CLI version command failed: $versionOutput" }
    $versionResult = $versionOutput -join [Environment]::NewLine | ConvertFrom-Json
    if (-not [bool]$versionResult.ok) { throw "Installed CLI returned ok=false." }

    $automationRoot = Join-Path $runRoot "gui-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $automationRoot -Force | Out-Null
    $state = Join-Path $automationRoot "state.json"
    $report = Join-Path $automationRoot "report.json"
    $screenshot = Join-Path $automationRoot "screenshot.png"
    $data = Join-Path $automationRoot "data"
    $arguments = @(
        "--automation-smoke",
        "--automation-state", "`"$state`"",
        "--automation-report", "`"$report`"",
        "--automation-screenshot", "`"$screenshot`"",
        "--automation-data-dir", "`"$data`""
    )
    $process = Start-Process -FilePath $gui -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        if (-not $process.HasExited) { $process.Kill($true) }
        throw "Installed GUI smoke timed out after $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
        throw "Installed GUI smoke failed with exit code $($process.ExitCode). State: $state"
    }
    $automationResult = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not [bool]$automationResult.passed) { throw "Installed GUI smoke checks failed." }
}


$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$registryPath = "HKLM:\Software\project-base-mirror\S3 Explorer"
$userRegistryPath = "HKCU:\Software\project-base-mirror\S3 Explorer"
$defaultInstallDirectory = Join-Path $env:ProgramFiles "S3 Explorer"

function Get-MsiQueryValue {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Query)
    $database = $view = $record = $null
    try {
        $database = $windowsInstaller.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $windowsInstaller, @($Path, 0))
        $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database, @($Query))
        $null = $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
        $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
        if ($null -eq $record) { return $null }
        return $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1)
    }
    finally {
        foreach ($object in @($record, $view, $database)) {
            if ($null -ne $object) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($object) }
        }
    }
}

function Get-MsiIdentity {
    param([Parameter(Mandatory)][string]$Path)
    $properties = @{}
    foreach ($name in @("ProductCode", "ProductVersion", "UpgradeCode", "INSTALLERFLAVOR", "S3EXPLORER_LIFECYCLE_FIXTURE")) {
        $properties[$name] = Get-MsiQueryValue -Path $Path -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'"
    }
    return [pscustomobject]$properties
}

function Assert-ProductState {
    param([Parameter(Mandatory)][string]$ProductCode, [Parameter(Mandatory)][bool]$Installed)
    $state = [int]$windowsInstaller.GetType().InvokeMember("ProductState", "GetProperty", $null, $windowsInstaller, @($ProductCode))
    $expected = if ($Installed) { 5 } else { -1 } # INSTALLSTATE_DEFAULT / INSTALLSTATE_UNKNOWN
    if ($state -ne $expected) { throw "MSI product $ProductCode has state $state, expected $expected." }
}

function Assert-InstallLocation {
    param([Parameter(Mandatory)][string]$Flavor)
    $registry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
    if ([string]$registry.InstallerFlavor -cne $Flavor) { throw "Installer flavor marker is not $Flavor." }
    if ([IO.Path]::GetFullPath([string]$registry.InstallLocation).TrimEnd('\') -ine $InstallDirectory) {
        throw "Installer registry location does not match the custom installation directory."
    }
    if ($InstallDirectory -ine $defaultInstallDirectory -and (Test-Path -LiteralPath $defaultInstallDirectory)) {
        throw "Upgrade unexpectedly created the default installation directory."
    }
}

function Get-PayloadSnapshot {
    $snapshot = @(Get-ChildItem -LiteralPath $InstallDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($InstallDirectory, $_.FullName)
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    if ($snapshot.Count -eq 0) { throw "Installed payload is empty." }
    return $snapshot
}

function Assert-FixturePayload {
    param([Parameter(Mandatory)][ValidateSet("previous", "current")][string]$Generation)
    $other = if ($Generation -eq "previous") { "current" } else { "previous" }
    $marker = (Get-Content -LiteralPath (Join-Path $InstallDirectory "lifecycle-payload.txt") -Raw).Trim()
    if ($marker -cne $Generation -or
        -not (Test-Path -LiteralPath (Join-Path $InstallDirectory "lifecycle-$Generation-only.txt")) -or
        (Test-Path -LiteralPath (Join-Path $InstallDirectory "lifecycle-$other-only.txt"))) {
        throw "Changed/added/removed fixture files do not match the $Generation generation."
    }
}

function Assert-RollbackLog {
    param([Parameter(Mandatory)][string]$Path)
    $log = Get-Content -LiteralPath $Path -Raw
    # A generic 1603 is insufficient: require removal and replacement to finish before
    # our precise failure action, followed by rollback, rather than a launch-condition error.
    # Windows Installer localizes action status lines to the package language (zh-CN).
    # Match equivalent English/Chinese lines while retaining exact actions/return codes/order.
    $patterns = @(
        '(?:Action ended [^\r\n]*RemoveExistingProducts\. Return value 1\.|操作结束 [^\r\n]*RemoveExistingProducts。返回值 1。)',
        '(?:Action ended [^\r\n]*InstallExecute\. Return value 1\.|操作结束 [^\r\n]*InstallExecute。返回值 1。)',
        '(?:Action start [^\r\n]*LifecycleForceRollback\.|操作开始 [^\r\n]*LifecycleForceRollback。)',
        'S3EXPLORER_LIFECYCLE_FORCE_ROLLBACK_AFTER_INSTALL_EXECUTE',
        '(?:Action ended [^\r\n]*LifecycleForceRollback\. Return value 3\.|操作结束 [^\r\n]*LifecycleForceRollback。返回值 3。)',
        '(?i)Rollback:'
    )
    $offset = 0
    foreach ($pattern in $patterns) {
        $match = [regex]::Match($log.Substring($offset), $pattern)
        if (-not $match.Success) { throw "Rollback log does not prove the expected action sequence: $pattern" }
        $offset += $match.Index + $match.Length
    }
}

$result = [ordered]@{
    status = "Running"
    currentMsi = $CurrentMsi
    previousMsi = $PreviousMsi
    rollbackMsi = $RollbackMsi
    installDirectory = $InstallDirectory
    rollback = "NotRequested"
    customDirectoryRetention = "NotRun"
    uninstall = "NotRun"
    logs = $runRoot
    userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}
$identities = @{}
$cleanupNeeded = $false
try {
    foreach ($entry in @(@("current", $CurrentMsi), @("previous", $PreviousMsi), @("rollback", $RollbackMsi))) {
        if (-not $entry[1]) { continue }
        $identities[$entry[0]] = Get-MsiIdentity -Path $entry[1]
        Assert-ProductState -ProductCode $identities[$entry[0]].ProductCode -Installed $false
    }
    $current = $identities.current
    foreach ($identity in $identities.Values) {
        if ($identity.UpgradeCode -ine $current.UpgradeCode -or $identity.INSTALLERFLAVOR -cne $current.INSTALLERFLAVOR) {
            throw "Lifecycle packages must share an UpgradeCode and installer flavor."
        }
    }
    if ($PreviousMsi -and ([version]$identities.previous.ProductVersion -ge [version]$current.ProductVersion -or
        $identities.previous.ProductCode -eq $current.ProductCode)) {
        throw "Upgrade test requires distinct product codes and an increasing MSI version."
    }
    if ($RollbackMsi) {
        foreach ($stage in @("previous", "current", "rollback")) {
            if ($identities[$stage].S3EXPLORER_LIFECYCLE_FIXTURE -cne $stage) {
                throw "Rollback tests require the generated $stage lifecycle fixture."
            }
        }
        if ($identities.rollback.ProductVersion -ne $current.ProductVersion -or
            $identities.rollback.ProductCode -eq $current.ProductCode -or
            $identities.rollback.ProductCode -eq $identities.previous.ProductCode) {
            throw "Rollback fixture must have the current version and its own product code."
        }
        foreach ($path in @($PreviousMsi, $CurrentMsi)) {
            if (Get-MsiQueryValue -Path $path -Query "SELECT ``Action`` FROM ``CustomAction`` WHERE ``Action``='LifecycleForceRollback'") {
                throw "Non-failing lifecycle package unexpectedly contains a failure hook."
            }
        }
        $failureType = Get-MsiQueryValue -Path $RollbackMsi -Query "SELECT ``Type`` FROM ``CustomAction`` WHERE ``Action``='LifecycleForceRollback'"
        $actionSequence = @{}
        foreach ($action in @("InstallInitialize", "RemoveExistingProducts", "InstallExecute", "LifecycleForceRollback", "InstallFinalize")) {
            $actionSequence[$action] = [int](Get-MsiQueryValue -Path $RollbackMsi -Query "SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action``='$action'")
        }
        if ([int]$failureType -ne 19 -or
            $actionSequence.InstallInitialize -ge $actionSequence.RemoveExistingProducts -or
            $actionSequence.RemoveExistingProducts -ge $actionSequence.InstallExecute -or
            $actionSequence.InstallExecute -ge $actionSequence.LifecycleForceRollback -or
            $actionSequence.LifecycleForceRollback -ge $actionSequence.InstallFinalize) {
            throw "Failure fixture is not sequenced after removal/replacement and before commit."
        }
        $result.rollback = "NotRun"
    }

    # Do not remove or replace an existing user's product. These tests are for clean VMs.
    $related = $windowsInstaller.GetType().InvokeMember("RelatedProducts", "GetProperty", $null, $windowsInstaller, @($current.UpgradeCode))
    foreach ($product in $related) { throw "Refusing lifecycle test: related product $product is already registered." }
    foreach ($path in @($InstallDirectory, $defaultInstallDirectory, $startMenuShortcutPath, $registryPath, $userRegistryPath)) {
        if (Test-Path -LiteralPath $path) { throw "Refusing lifecycle test on an existing path: $path" }
    }
    $result.products = $identities
    $cleanupNeeded = $true
    if ($PreviousMsi) {
        $null = Invoke-Msi -Action Install -Path $PreviousMsi -LogPath (Join-Path $runRoot "install-previous.log")
        Assert-ProductState -ProductCode $identities.previous.ProductCode -Installed $true
        Assert-InstallLocation -Flavor $current.INSTALLERFLAVOR
        Test-InstalledApplication -RequireUpdater -RequireStableStartMenuShortcut
    }

    if ($RollbackMsi) {
        Assert-FixturePayload -Generation previous
        $before = @(Get-PayloadSnapshot)
        $before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "payload-before-rollback.json") -Encoding utf8
        $rollbackLog = Join-Path $runRoot "upgrade-forced-failure.log"
        $result.forcedFailureExitCode = Invoke-Msi -Action Install -Path $RollbackMsi -LogPath $rollbackLog -ReuseRegisteredDirectory -ExpectedExitCodes @(1603)
        Assert-RollbackLog -Path $rollbackLog
        Assert-ProductState -ProductCode $identities.previous.ProductCode -Installed $true
        Assert-ProductState -ProductCode $identities.rollback.ProductCode -Installed $false
        Assert-ProductState -ProductCode $current.ProductCode -Installed $false
        Assert-InstallLocation -Flavor $current.INSTALLERFLAVOR
        Assert-FixturePayload -Generation previous
        $after = @(Get-PayloadSnapshot)
        $after | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "payload-after-rollback.json") -Encoding utf8
        if (($before | ConvertTo-Json -Depth 4 -Compress) -cne ($after | ConvertTo-Json -Depth 4 -Compress)) {
            throw "Rollback did not restore the exact previous payload and file set."
        }
        Test-InstalledApplication -RequireUpdater -RequireStableStartMenuShortcut
        $result.rollback = "Passed"
    }

    # Deliberately omit APPLICATIONFOLDER on upgrade: MSI must recover the custom path.
    $null = Invoke-Msi -Action Install -Path $CurrentMsi -LogPath (Join-Path $runRoot "install-current.log") -ReuseRegisteredDirectory:([bool]$PreviousMsi)
    Assert-ProductState -ProductCode $current.ProductCode -Installed $true
    if ($PreviousMsi) { Assert-ProductState -ProductCode $identities.previous.ProductCode -Installed $false }
    Assert-InstallLocation -Flavor $current.INSTALLERFLAVOR
    if ($RollbackMsi) { Assert-FixturePayload -Generation current }
    Test-InstalledApplication -RequireUpdater -RequireStableStartMenuShortcut
    $result.customDirectoryRetention = if ($PreviousMsi) { "Passed" } else { "NotRequested" }

    $null = Invoke-Msi -Action Uninstall -Path $CurrentMsi -LogPath (Join-Path $runRoot "uninstall-current.log")
    foreach ($identity in $identities.Values) { Assert-ProductState -ProductCode $identity.ProductCode -Installed $false }
    foreach ($name in @("S3Explorer.exe", "s3explorer-cli.exe", "S3Explorer.Updater.exe", "lifecycle-payload.txt", "lifecycle-current-only.txt")) {
        if (Test-Path -LiteralPath (Join-Path $InstallDirectory $name)) { throw "$name remains after uninstall." }
    }
    if ((Test-Path -LiteralPath $InstallDirectory) -and @(Get-ChildItem -LiteralPath $InstallDirectory -Recurse -File).Count -ne 0) {
        throw "Installed payload files remain after uninstall."
    }
    if (Test-Path -LiteralPath $startMenuShortcutPath) { throw "Start menu shortcut remains after uninstall." }
    foreach ($name in @("StartMenuShortcut", "DesktopShortcut")) {
        $marker = Get-ItemPropertyValue -LiteralPath $userRegistryPath -Name $name -ErrorAction SilentlyContinue
        if ($null -ne $marker) { throw "Per-user $name marker remains after uninstall." }
    }
    foreach ($name in @("InstallerFlavor", "InstallLocation")) {
        $marker = Get-ItemPropertyValue -LiteralPath $registryPath -Name $name -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace([string]$marker)) { throw "$name marker remains after uninstall." }
    }
    $cleanupNeeded = $false
    $result.uninstall = "Passed"
    $result.status = "Passed"
    Write-Host "Installer lifecycle passed. Logs: $runRoot"
}
catch {
    $result.status = "Failed"
    $result.error = $_.Exception.Message
    throw
}
finally {
    if ($cleanupNeeded) {
        foreach ($stage in @("rollback", "current", "previous")) {
            if (-not $identities.ContainsKey($stage)) { continue }
            $productCode = $identities[$stage].ProductCode
            try {
                $state = [int]$windowsInstaller.GetType().InvokeMember("ProductState", "GetProperty", $null, $windowsInstaller, @($productCode))
                if ($state -ne -1) {
                    $null = Invoke-Msi -Action Uninstall -Path $productCode -LogPath (Join-Path $runRoot "cleanup-$stage.log")
                }
            }
            catch { Write-Warning "Cleanup for $stage failed: $($_.Exception.Message)" }
        }
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot "result.json") -Encoding utf8
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller)
}
