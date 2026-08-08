[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("Start", "Status", "Stop", "Smoke", "CorruptSmoke", "SingleInstanceSmoke", "Version", "Help")]
    [string]$Command = "Help",

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq ("S3ExplorerAutomationNative" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class S3ExplorerAutomationNative
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr windowHandle);
}
"@
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $repositoryRoot "S3Explorer.sln"
$applicationPath = Join-Path $repositoryRoot "src\S3Explorer.App\bin\Release\net10.0-windows\win-x64\S3Explorer.exe"
$automationRoot = Join-Path $repositoryRoot "artifacts\automation"
$currentRoot = Join-Path $automationRoot "current"
$statePath = Join-Path $currentRoot "state.json"
$reportPath = Join-Path $currentRoot "report.json"
$screenshotPath = Join-Path $currentRoot "screenshot.png"
$dataPath = Join-Path $currentRoot "data"

function Write-JsonResult {
    param([Parameter(Mandatory)]$Value)
    $Value | ConvertTo-Json -Depth 12 -Compress
}

function Ensure-ApplicationBuild {
    if (Test-Path -LiteralPath $applicationPath) {
        return
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found on PATH."
    }

    & dotnet build $solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $applicationPath)) {
        throw "Application executable was not produced at the fixed Release path."
    }
}

function Read-AutomationState {
    param([string]$Path = $statePath)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Automation state is invalid JSON: $Path"
    }
}

function Get-VerifiedApplicationProcess {
    param($State = (Read-AutomationState))

    if ($null -eq $State -or [int]$State.pid -le 0) {
        return $null
    }

    $process = Get-Process -Id ([int]$State.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }

    try {
        $expectedPath = [System.IO.Path]::GetFullPath([string]$State.processPath)
        $actualPath = [System.IO.Path]::GetFullPath($process.Path)
        $expectedStart = [DateTimeOffset]::Parse([string]$State.processStartTimeUtc).UtcDateTime
        $actualStart = $process.StartTime.ToUniversalTime()
        if (-not [string]::Equals($expectedPath, $actualPath, [StringComparison]::OrdinalIgnoreCase) -or
            [Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 2) {
            return $null
        }
        return $process
    }
    catch {
        return $null
    }
}

function Wait-AutomationState {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][int]$Timeout
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        $state = Read-AutomationState -Path $Path
        if ($null -ne $state -and $Expected -contains [string]$state.status) {
            return $state
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for application state: $($Expected -join ', ')."
}

function Wait-WindowVisibility {
    param(
        [Parameter(Mandatory)][IntPtr]$WindowHandle,
        [Parameter(Mandatory)][bool]$Visible,
        [Parameter(Mandatory)][int]$Timeout
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        if ([S3ExplorerAutomationNative]::IsWindowVisible($WindowHandle) -eq $Visible) {
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for window visibility=$Visible."
}

function Start-ApplicationProcess {
    param(
        [Parameter(Mandatory)][string]$State,
        [Parameter(Mandatory)][string]$Report,
        [Parameter(Mandatory)][string]$Screenshot,
        [Parameter(Mandatory)][string]$Data,
        [switch]$Smoke,
        [string]$InstanceKey = ""
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $applicationPath
    $startInfo.WorkingDirectory = Split-Path -Parent $applicationPath
    $startInfo.UseShellExecute = $true
    if ($Smoke) {
        [void]$startInfo.ArgumentList.Add("--automation-smoke")
    }
    if (-not [string]::IsNullOrWhiteSpace($InstanceKey)) {
        [void]$startInfo.ArgumentList.Add("--automation-instance-key")
        [void]$startInfo.ArgumentList.Add($InstanceKey)
    }
    foreach ($argument in @(
        "--automation-state", $State,
        "--automation-report", $Report,
        "--automation-screenshot", $Screenshot,
        "--automation-data-dir", $Data
    )) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Application process could not be started."
    }
    return $process
}

switch ($Command) {
    "Start" {
        Ensure-ApplicationBuild
        New-Item -ItemType Directory -Path $currentRoot -Force | Out-Null
        $existingState = Read-AutomationState
        $existingProcess = Get-VerifiedApplicationProcess -State $existingState
        if ($null -ne $existingProcess) {
            Write-JsonResult ([ordered]@{
                command = "Start"
                status = [string]$existingState.status
                running = $true
                pid = $existingProcess.Id
                statePath = $statePath
                reportPath = $reportPath
                screenshotPath = $screenshotPath
            })
            break
        }

        foreach ($path in @($statePath, $reportPath, $screenshotPath)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }

        $process = Start-ApplicationProcess -State $statePath -Report $reportPath -Screenshot $screenshotPath -Data $dataPath
        $state = Wait-AutomationState -Path $statePath -Expected @("ready", "failed") -Timeout $TimeoutSeconds
        if ([string]$state.status -eq "failed" -or -not [bool]$state.passed) {
            throw "Application startup validation failed: $($state.error)"
        }
        $verified = Get-VerifiedApplicationProcess -State $state
        if ($null -eq $verified) {
            throw "Application reached ready state but process identity verification failed."
        }

        Write-JsonResult ([ordered]@{
            command = "Start"
            status = "ready"
            running = $true
            pid = $verified.Id
            title = [string]$state.title
            statePath = $statePath
            reportPath = $reportPath
            screenshotPath = $screenshotPath
        })
    }

    "Status" {
        $state = Read-AutomationState
        $process = Get-VerifiedApplicationProcess -State $state
        Write-JsonResult ([ordered]@{
            command = "Status"
            status = if ($null -eq $state) { "not-started" } elseif ($null -eq $process) { "stopped" } else { [string]$state.status }
            running = $null -ne $process
            pid = if ($null -eq $process) { 0 } else { $process.Id }
            title = if ($null -eq $state) { "" } else { [string]$state.title }
            passed = $null -ne $state -and [bool]$state.passed
            statePath = $statePath
            reportPath = $reportPath
            screenshotPath = $screenshotPath
        })
    }

    "Stop" {
        $state = Read-AutomationState
        $process = Get-VerifiedApplicationProcess -State $state
        if ($null -eq $process) {
            Write-JsonResult ([ordered]@{ command = "Stop"; status = "stopped"; running = $false; pid = 0 })
            break
        }

        if (-not $process.CloseMainWindow()) {
            throw "Application main window did not accept a close request."
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Application did not exit after the graceful close request."
        }

        Write-JsonResult ([ordered]@{ command = "Stop"; status = "stopped"; running = $false; pid = $process.Id })
    }

    "Smoke" {
        Ensure-ApplicationBuild
        $runRoot = Join-Path $automationRoot ("smoke-" + [Guid]::NewGuid().ToString("N"))
        $runState = Join-Path $runRoot "state.json"
        $runReport = Join-Path $runRoot "report.json"
        $runScreenshot = Join-Path $runRoot "screenshot.png"
        $runData = Join-Path $runRoot "data"
        New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

        $process = Start-ApplicationProcess -State $runState -Report $runReport -Screenshot $runScreenshot -Data $runData -Smoke
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            [void]$process.CloseMainWindow()
            throw "UI smoke process timed out."
        }
        if ($process.ExitCode -ne 0) {
            $failedState = Read-AutomationState -Path $runState
            throw "UI smoke process failed with exit code $($process.ExitCode): $($failedState.error)"
        }
        if (-not (Test-Path -LiteralPath $runReport) -or -not (Test-Path -LiteralPath $runScreenshot)) {
            throw "UI smoke did not produce its report and screenshot."
        }

        $report = Get-Content -LiteralPath $runReport -Raw | ConvertFrom-Json
        if (-not [bool]$report.passed) {
            $failedChecks = @($report.checks | Where-Object { -not [bool]$_.passed } | ForEach-Object { $_.name })
            throw "UI smoke checks failed: $($failedChecks -join ', ')."
        }
        if ((Get-Item -LiteralPath $runScreenshot).Length -le 0) {
            throw "UI smoke screenshot is empty."
        }

        Write-JsonResult ([ordered]@{
            command = "Smoke"
            status = "passed"
            passed = $true
            checks = @($report.checks).Count
            reportPath = $runReport
            screenshotPath = $runScreenshot
        })
    }

    "CorruptSmoke" {
        Ensure-ApplicationBuild
        $runRoot = Join-Path $automationRoot ("corrupt-smoke-" + [Guid]::NewGuid().ToString("N"))
        $runState = Join-Path $runRoot "state.json"
        $runReport = Join-Path $runRoot "report.json"
        $runScreenshot = Join-Path $runRoot "screenshot.png"
        $runData = Join-Path $runRoot "data"
        New-Item -ItemType Directory -Path $runData -Force | Out-Null
        foreach ($name in @(
            "profiles.json",
            "settings.json",
            "transfers.json",
            "cdn-config.json",
            "cdn-credentials.json",
            "cdn-jobs.json"
        )) {
            Set-Content -LiteralPath (Join-Path $runData $name) -Value "{truncated" -Encoding utf8
        }

        $process = Start-ApplicationProcess -State $runState -Report $runReport -Screenshot $runScreenshot -Data $runData -Smoke
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            [void]$process.CloseMainWindow()
            throw "Corrupt-state UI smoke process timed out."
        }
        if ($process.ExitCode -ne 0) {
            $failedState = Read-AutomationState -Path $runState
            throw "Corrupt-state UI smoke failed with exit code $($process.ExitCode): $($failedState.error)"
        }

        $report = Get-Content -LiteralPath $runReport -Raw | ConvertFrom-Json
        if (-not [bool]$report.passed) {
            throw "Corrupt-state UI smoke did not reach a usable main window."
        }
        $preserved = @(Get-ChildItem -LiteralPath $runData -File -Filter "*.corrupt-*" -ErrorAction SilentlyContinue)
        if ($preserved.Count -lt 6) {
            throw "Corrupt-state UI smoke preserved only $($preserved.Count) of 6 malformed stores."
        }

        Write-JsonResult ([ordered]@{
            command = "CorruptSmoke"
            status = "passed"
            passed = $true
            checks = @($report.checks).Count
            preservedCorruptStores = $preserved.Count
            reportPath = $runReport
            screenshotPath = $runScreenshot
        })
    }

    "SingleInstanceSmoke" {
        Ensure-ApplicationBuild
        $runRoot = Join-Path $automationRoot ("single-instance-" + [Guid]::NewGuid().ToString("N"))
        $runState = Join-Path $runRoot "state.json"
        $runReport = Join-Path $runRoot "report.json"
        $runScreenshot = Join-Path $runRoot "screenshot.png"
        $runData = Join-Path $runRoot "data"
        $instanceKey = "automation." + [Guid]::NewGuid().ToString("N")
        New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

        $primary = $null
        try {
            $primary = Start-ApplicationProcess -State $runState -Report $runReport -Screenshot $runScreenshot -Data $runData -InstanceKey $instanceKey
            $state = Wait-AutomationState -Path $runState -Expected @("ready", "failed") -Timeout $TimeoutSeconds
            if ([string]$state.status -eq "failed" -or -not [bool]$state.passed) {
                throw "Primary single-instance startup failed: $($state.error)"
            }
            $verifiedPrimary = Get-VerifiedApplicationProcess -State $state
            if ($null -eq $verifiedPrimary -or $verifiedPrimary.Id -ne $primary.Id) {
                throw "Primary single-instance process identity verification failed."
            }

            $windowHandle = [IntPtr][long]$state.windowHandle
            if ($windowHandle -eq [IntPtr]::Zero) {
                throw "Primary single-instance window handle is missing."
            }
            [void][S3ExplorerAutomationNative]::ShowWindow($windowHandle, 0)
            Wait-WindowVisibility -WindowHandle $windowHandle -Visible $false -Timeout $TimeoutSeconds

            $secondaryExitCodes = @()
            foreach ($attempt in 1..2) {
                $secondary = Start-ApplicationProcess -State $runState -Report $runReport -Screenshot $runScreenshot -Data $runData -InstanceKey $instanceKey
                if (-not $secondary.WaitForExit($TimeoutSeconds * 1000)) {
                    throw "Secondary single-instance process $attempt did not exit."
                }
                $secondaryExitCodes += $secondary.ExitCode
                if ($secondary.ExitCode -ne 0) {
                    throw "Secondary single-instance process $attempt exited with code $($secondary.ExitCode)."
                }
                if ($secondary.Id -eq $primary.Id) {
                    throw "Secondary single-instance process reused the primary PID unexpectedly."
                }
                if ($attempt -eq 1) {
                    Wait-WindowVisibility -WindowHandle $windowHandle -Visible $true -Timeout $TimeoutSeconds
                }
            }

            $stateAfterActivation = Read-AutomationState -Path $runState
            $verifiedAfterActivation = Get-VerifiedApplicationProcess -State $stateAfterActivation
            if ($null -eq $verifiedAfterActivation -or $verifiedAfterActivation.Id -ne $primary.Id) {
                throw "Primary process identity changed after secondary activation."
            }

            Write-JsonResult ([ordered]@{
                command = "SingleInstanceSmoke"
                status = "passed"
                passed = $true
                primaryPid = $primary.Id
                secondaryExitCodes = $secondaryExitCodes
                existingWindowRestored = $true
                instanceKey = $instanceKey
                statePath = $runState
            })
        }
        finally {
            if ($null -ne $primary -and -not $primary.HasExited) {
                [void]$primary.CloseMainWindow()
                if (-not $primary.WaitForExit($TimeoutSeconds * 1000)) {
                    throw "Primary single-instance process did not exit cleanly."
                }
            }
        }
    }

    "Version" {
        [xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
        Write-JsonResult ([ordered]@{ command = "Version"; version = [string]$properties.Project.PropertyGroup.Version })
    }

    "Help" {
        Write-JsonResult ([ordered]@{
            command = "Help"
            commands = @(
                "Start  - build if needed, launch the app and wait for UI readiness",
                "Status - report verified process and UI state",
                "Stop   - gracefully close the verified app process",
                "Smoke  - run isolated UI checks and create a screenshot",
                "CorruptSmoke - prove malformed user-state files do not block startup",
                "SingleInstanceSmoke - launch three times and verify one process remains",
                "Version - print the repository application version",
                "Help   - print this fixed command list"
            )
        })
    }
}
