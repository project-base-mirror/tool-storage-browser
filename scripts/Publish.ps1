[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipValidation,
    [switch]$MeasureRuntime,
    [switch]$NoOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $repositoryRoot "S3Explorer.sln"
$appProject = Join-Path $repositoryRoot "src\S3Explorer.App\S3Explorer.App.csproj"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$outputRoot = Join-Path $artifactsRoot "release"
$frameworkName = "S3Explorer-$Runtime"
$selfContainedName = "S3Explorer-$Runtime-self-contained"
$frameworkDirectory = Join-Path $outputRoot $frameworkName
$selfContainedDirectory = Join-Path $outputRoot $selfContainedName
$frameworkZip = Join-Path $outputRoot "$frameworkName.zip"
$selfContainedZip = Join-Path $outputRoot "$selfContainedName.zip"
$metricsPath = Join-Path $outputRoot "release-metrics.json"

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Get-DirectorySize {
    param([Parameter(Mandatory)][string]$Path)

    $measurement = Get-ChildItem -LiteralPath $Path -File -Recurse | Measure-Object -Property Length -Sum
    if ($null -eq $measurement.Sum) {
        return [int64]0
    }

    return [int64]$measurement.Sum
}

function New-PackageMetric {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][bool]$SelfContained
    )

    $directoryBytes = Get-DirectorySize -Path $Directory
    $zipFile = Get-Item -LiteralPath $ZipPath
    return [ordered]@{
        name = $Name
        selfContained = $SelfContained
        directoryBytes = $directoryBytes
        directoryMiB = [Math]::Round($directoryBytes / 1MB, 2)
        zipBytes = [int64]$zipFile.Length
        zipMiB = [Math]::Round($zipFile.Length / 1MB, 2)
        zipSha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Publish-Package {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][bool]$SelfContained
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    $selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
    Invoke-DotNet -Arguments @(
        "publish",
        $appProject,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-o", $Destination,
        "-p:PublishTrimmed=false",
        "-p:PublishSingleFile=false",
        "-p:PublishReadyToRun=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
}

function New-ZipArchive {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $DestinationPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

function Measure-ApplicationRuntime {
    param([Parameter(Mandatory)][string]$Executable)

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -PassThru
    $startupMilliseconds = $null
    $workingSetBytes = $null

    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $startupMilliseconds = $stopwatch.ElapsedMilliseconds
                break
            }
            Start-Sleep -Milliseconds 100
        }

        if ($null -eq $startupMilliseconds) {
            throw "S3 Explorer did not create a main window within 20 seconds."
        }

        Start-Sleep -Seconds 2
        $process.Refresh()
        $workingSetBytes = [int64]$process.WorkingSet64
    }
    finally {
        $stopwatch.Stop()
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }
        $process.Dispose()
    }

    return [ordered]@{
        startupMilliseconds = [int64]$startupMilliseconds
        idleWorkingSetBytes = $workingSetBytes
        idleWorkingSetMiB = [Math]::Round($workingSetBytes / 1MB, 2)
        stabilizationDelaySeconds = 2
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found on PATH. Install the .NET 10 SDK first."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not $SkipValidation) {
    Invoke-DotNet -Arguments @("restore", $solution)
    Invoke-DotNet -Arguments @("test", $solution, "-c", $Configuration, "--no-restore")
    Invoke-DotNet -Arguments @("build", $solution, "-c", $Configuration, "--no-restore")
}

Publish-Package -Destination $frameworkDirectory -SelfContained $false
Publish-Package -Destination $selfContainedDirectory -SelfContained $true
New-ZipArchive -SourceDirectory $frameworkDirectory -DestinationPath $frameworkZip
New-ZipArchive -SourceDirectory $selfContainedDirectory -DestinationPath $selfContainedZip

$runtimeMetric = $null
if ($MeasureRuntime) {
    $runtimeMetric = Measure-ApplicationRuntime -Executable (Join-Path $frameworkDirectory "S3Explorer.exe")
}

$metrics = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    configuration = $Configuration
    runtime = $Runtime
    dotnetSdkVersion = (& dotnet --version).Trim()
    trimmingEnabled = $false
    singleFileEnabled = $false
    packages = @(
        (New-PackageMetric -Name $frameworkName -Directory $frameworkDirectory -ZipPath $frameworkZip -SelfContained $false),
        (New-PackageMetric -Name $selfContainedName -Directory $selfContainedDirectory -ZipPath $selfContainedZip -SelfContained $true)
    )
    runtimeMeasurement = $runtimeMetric
}

$metrics | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metricsPath -Encoding utf8

Write-Host ""
$resolvedOutputRoot = (Resolve-Path -LiteralPath $outputRoot).Path
Write-Host "Release artifacts: $resolvedOutputRoot"
$metrics.packages | Format-Table name, directoryMiB, zipMiB, zipSha256 -AutoSize
if ($null -ne $runtimeMetric) {
    $runtimeMetric | Format-List
}

if (-not $NoOpen) {
    Start-Process -FilePath "explorer.exe" -ArgumentList @($resolvedOutputRoot) | Out-Null
}
