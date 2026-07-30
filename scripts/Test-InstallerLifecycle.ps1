[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CurrentMsi,
    [string]$PreviousMsi = "",
    [string]$InstallDirectory = "",
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$CurrentMsi = (Resolve-Path -LiteralPath $CurrentMsi).Path
if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
    $PreviousMsi = (Resolve-Path -LiteralPath $PreviousMsi).Path
}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:ProgramFiles "S3 Explorer Lifecycle Test"
}
$runRoot = Join-Path $repositoryRoot "artifacts\installer-lifecycle\$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Invoke-Msi {
    param(
        [Parameter(Mandatory)][ValidateSet("Install", "Uninstall")][string]$Action,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$LogPath
    )
    $verb = if ($Action -eq "Install") { "/i" } else { "/x" }
    $arguments = @($verb, "`"$Path`"", "/qn", "/norestart", "/l*v", "`"$LogPath`"")
    if ($Action -eq "Install") {
        $arguments += "APPLICATIONFOLDER=`"$InstallDirectory`""
        $arguments += "INSTALLDESKTOPSHORTCUT=0"
    }
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
        -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "$Action failed with MSI exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Test-InstalledApplication {
    $gui = Join-Path $InstallDirectory "S3Explorer.exe"
    $cli = Join-Path $InstallDirectory "s3explorer-cli.exe"
    if (-not (Test-Path -LiteralPath $gui) -or -not (Test-Path -LiteralPath $cli)) {
        throw "Installed GUI or CLI executable is missing from $InstallDirectory."
    }

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
        "--automation-state", $state,
        "--automation-report", $report,
        "--automation-screenshot", $screenshot,
        "--automation-data-dir", $data
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

$installed = $false
try {
    if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
        Invoke-Msi -Action Install -Path $PreviousMsi -LogPath (Join-Path $runRoot "install-previous.log")
        $installed = $true
        Test-InstalledApplication
    }

    Invoke-Msi -Action Install -Path $CurrentMsi -LogPath (Join-Path $runRoot "install-current.log")
    $installed = $true
    Test-InstalledApplication

    $registry = Get-ItemProperty -LiteralPath "HKLM:\Software\project-base-mirror\S3 Explorer" -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace([string]$registry.InstallerFlavor)) {
        throw "Installer flavor registry marker was not written."
    }
    if ([IO.Path]::GetFullPath([string]$registry.InstallLocation).TrimEnd('\') -cne
        [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')) {
        throw "Installer registry location does not match the actual test installation."
    }

    Invoke-Msi -Action Uninstall -Path $CurrentMsi -LogPath (Join-Path $runRoot "uninstall-current.log")
    $installed = $false
    if (Test-Path -LiteralPath (Join-Path $InstallDirectory "S3Explorer.exe")) {
        throw "GUI executable remains after uninstall."
    }
    $remainingMarker = Get-ItemPropertyValue `
        -LiteralPath "HKLM:\Software\project-base-mirror\S3 Explorer" `
        -Name "InstallerFlavor" `
        -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace([string]$remainingMarker)) {
        throw "Installer flavor marker remains after uninstall."
    }

    [ordered]@{
        status = "Passed"
        currentMsi = $CurrentMsi
        previousMsi = $PreviousMsi
        installDirectory = $InstallDirectory
        logs = $runRoot
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "result.json") -Encoding utf8
    Write-Host "Installer lifecycle passed. Logs: $runRoot"
}
finally {
    if ($installed) {
        try { Invoke-Msi -Action Uninstall -Path $CurrentMsi -LogPath (Join-Path $runRoot "cleanup-current.log") }
        catch {
            if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
                try { Invoke-Msi -Action Uninstall -Path $PreviousMsi -LogPath (Join-Path $runRoot "cleanup-previous.log") }
                catch { Write-Warning $_.Exception.Message }
            }
        }
    }
}
