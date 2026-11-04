[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("self-contained", "framework-dependent")]
    [string]$Flavor,
    [string]$Configuration = "Release"
)

# Build test-only MSI versions from the production package source and real app payload.
# No Release assets, release tags, credentials, or production storage are used.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "Installer fixtures must be built on Windows." }

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fixtureRoot = Join-Path $repositoryRoot "artifacts\installer-lifecycle-fixtures\$Flavor"
if (Test-Path -LiteralPath $fixtureRoot) {
    throw "Fixture output already exists; use a clean workspace: $fixtureRoot"
}
$payload = Join-Path $fixtureRoot "payload"
New-Item -ItemType Directory -Path $payload -Force | Out-Null

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

$selfContained = ($Flavor -eq "self-contained").ToString().ToLowerInvariant()
foreach ($project in @("S3Explorer.App", "S3Explorer.Cli")) {
    Invoke-DotNet -Arguments @(
        "publish", (Join-Path $repositoryRoot "src\$project\$project.csproj"),
        "-c", $Configuration, "--no-restore", "--self-contained", $selfContained,
        "-o", $payload, "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
        "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false"
    )
}
$updaterPayload = Join-Path $fixtureRoot "updater"
Invoke-DotNet -Arguments @(
    "publish", (Join-Path $repositoryRoot "src\S3Explorer.Updater\S3Explorer.Updater.csproj"),
    "-c", $Configuration, "--no-restore", "--self-contained", "true", "-o", $updaterPayload,
    "-p:PublishSingleFile=true", "-p:PublishTrimmed=true", "-p:TrimMode=full",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false"
)
Copy-Item -LiteralPath (Join-Path $updaterPayload "S3Explorer.Updater.exe") -Destination $payload

$packages = @{}
foreach ($stage in @("previous", "current", "rollback")) {
    $stageRoot = Join-Path $fixtureRoot $stage
    $stagePayload = Join-Path $stageRoot "payload"
    $projectRoot = Join-Path $stageRoot "project"
    $output = Join-Path $stageRoot "package"
    New-Item -ItemType Directory -Path $stagePayload, $projectRoot, $output -Force | Out-Null
    Copy-Item -Path (Join-Path $payload "*") -Destination $stagePayload -Recurse
    $payloadGeneration = if ($stage -eq "previous") { "previous" } else { "current" }
    Set-Content -LiteralPath (Join-Path $stagePayload "lifecycle-payload.txt") -Value $payloadGeneration -Encoding utf8
    Set-Content -LiteralPath (Join-Path $stagePayload "lifecycle-$payloadGeneration-only.txt") -Value $payloadGeneration -Encoding utf8

    foreach ($file in @("S3Explorer.Installer.wixproj", "Package.wxs", "packages.lock.json")) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "installer\$file") -Destination $projectRoot
    }
    $packagePath = Join-Path $projectRoot "Package.wxs"
    [xml]$source = Get-Content -LiteralPath $packagePath -Raw
    $namespace = $source.DocumentElement.NamespaceURI
    $package = $source.DocumentElement.FirstChild
    $fixtureMarker = $source.CreateElement("Property", $namespace)
    $fixtureMarker.SetAttribute("Id", "S3EXPLORER_LIFECYCLE_FIXTURE")
    $fixtureMarker.SetAttribute("Value", $stage)
    $null = $package.AppendChild($fixtureMarker)
    if ($stage -eq "rollback") {
        # The error action exists ONLY in this generated copy, never in installer/Package.wxs.
        # InstallExecute runs all spooled file/registry/shortcut operations. Failing after it
        # but before InstallFinalize exercises rollback after both removal and replacement.
        # https://learn.microsoft.com/windows/win32/msi/installexecute-action
        $action = $source.CreateElement("CustomAction", $namespace)
        $action.SetAttribute("Id", "LifecycleForceRollback")
        $action.SetAttribute("Error", "S3EXPLORER_LIFECYCLE_FORCE_ROLLBACK_AFTER_INSTALL_EXECUTE")
        $null = $package.AppendChild($action)
        $sequence = $source.CreateElement("InstallExecuteSequence", $namespace)
        # Referencing InstallExecute pulls in WiX 5's standard action at sequence 6500.
        # Do not redeclare it: that conflicts with the standard library's virtual symbol.
        $custom = $source.CreateElement("Custom", $namespace)
        $custom.SetAttribute("Action", "LifecycleForceRollback")
        $custom.SetAttribute("After", "InstallExecute")
        $custom.SetAttribute("Condition", "NOT Installed")
        $null = $sequence.AppendChild($custom)
        $null = $package.AppendChild($sequence)
    }
    $source.Save($packagePath)
    $version = if ($stage -eq "previous") { "0.0.1" } else { "0.0.2" }
    Invoke-DotNet -Arguments @(
        "build", (Join-Path $projectRoot "S3Explorer.Installer.wixproj"), "-c", $Configuration,
        "-p:RestoreLockedMode=true", "-p:Version=$version", "-p:PayloadDir=$stagePayload",
        "-p:InstallerFlavor=$Flavor", "-p:InstallerOutputName=lifecycle-$stage",
        "-p:ApplicationIconPath=$(Join-Path $repositoryRoot 'src\S3Explorer.App\Assets\S3Explorer.ico')",
        "-p:OutputPath=$output"
    )
    $packages[$stage] = (Resolve-Path -LiteralPath (Join-Path $output "lifecycle-$stage.msi")).Path
}

[ordered]@{
    schemaVersion = 1
    flavor = $Flavor
    previousMsi = $packages.previous
    currentMsi = $packages.current
    rollbackMsi = $packages.rollback
    packageSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot "installer\Package.wxs") -Algorithm SHA256).Hash
    scope = "Real current application payload; synthetic MSI versions 0.0.1 and 0.0.2, with changed/added/removed lifecycle files. Not historical-release compatibility evidence."
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot "fixtures.json") -Encoding utf8
Write-Host "Test-only installer fixtures: $fixtureRoot"
