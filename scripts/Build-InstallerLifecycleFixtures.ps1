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

# The other flavor has a real matching payload, not just a changed registry label.
$alternateFlavor = if ($Flavor -eq "self-contained") { "framework-dependent" } else { "self-contained" }
$alternatePayload = Join-Path $fixtureRoot "alternate-payload"
New-Item -ItemType Directory -Path $alternatePayload -Force | Out-Null
foreach ($project in @("S3Explorer.App", "S3Explorer.Cli")) {
    Invoke-DotNet -Arguments @(
        "publish", (Join-Path $repositoryRoot "src\$project\$project.csproj"),
        "-c", $Configuration, "--no-restore", "--self-contained", ($alternateFlavor -eq "self-contained").ToString().ToLowerInvariant(),
        "-o", $alternatePayload, "-p:PublishSingleFile=false", "-p:PublishTrimmed=false",
        "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false"
    )
}
Copy-Item -LiteralPath (Join-Path $updaterPayload "S3Explorer.Updater.exe") -Destination $alternatePayload

# Build the actual historical updater source, not a modern updater with a simulated
# command line. These immutable release commits have identical updater and MSI source.
# Do not download historical Release assets; fixtures keep synthetic MSI versions.
$legacyCommits = @("c7a04cc6d3d549b06033dc66ed688102a845d971", "747bd974f729535e3505ae32c925f73366d3b118")
& git -C $repositoryRoot diff --exit-code $legacyCommits[0] $legacyCommits[1] -- src/S3Explorer.Updater installer/Package.wxs
if ($LASTEXITCODE -ne 0) { throw "v0.8.8/v0.8.9 legacy sources are missing or no longer identical." }
function Read-LegacySource {
    param([Parameter(Mandatory)][string]$Path)
    $content = & git -C $repositoryRoot show "$($legacyCommits[0]):$Path"
    if ($LASTEXITCODE -ne 0) { throw "Missing pinned historical source: $Path. Check out full repository history." }
    return ($content -join "`n") + "`n"
}
$legacyRoot = Join-Path $fixtureRoot "legacy-updater-source"
$legacyProjectRoot = Join-Path $legacyRoot "src\S3Explorer.Updater"
$legacyUpdaterPayload = Join-Path $fixtureRoot "legacy-updater"
New-Item -ItemType Directory -Path $legacyProjectRoot -Force | Out-Null
foreach ($file in @("Directory.Build.props", "global.json")) {
    Set-Content -LiteralPath (Join-Path $legacyRoot $file) -Value (Read-LegacySource -Path $file) -NoNewline -Encoding utf8
}
foreach ($file in @("Program.cs", "AssemblyInfo.cs", "S3Explorer.Updater.csproj", "packages.lock.json")) {
    Set-Content -LiteralPath (Join-Path $legacyProjectRoot $file) -Value (Read-LegacySource -Path "src/S3Explorer.Updater/$file") -NoNewline -Encoding utf8
}
Invoke-DotNet -Arguments @(
    "publish", (Join-Path $legacyProjectRoot "S3Explorer.Updater.csproj"),
    "-c", $Configuration, "--self-contained", "true", "-o", $legacyUpdaterPayload,
    "-p:RestoreLockedMode=true", "-p:PublishSingleFile=true", "-p:PublishTrimmed=true", "-p:TrimMode=full",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:PublishReadyToRun=false", "-p:DebugType=None", "-p:DebugSymbols=false"
)

$packages = @{}
foreach ($stage in @("previous", "current", "rollback", "alternate", "legacy-incompatible")) {
    $stageRoot = Join-Path $fixtureRoot $stage
    $stagePayload = Join-Path $stageRoot "payload"
    $projectRoot = Join-Path $stageRoot "project"
    $output = Join-Path $stageRoot "package"
    New-Item -ItemType Directory -Path $stagePayload, $projectRoot, $output -Force | Out-Null
    $sourcePayload = if ($stage -eq "alternate") { $alternatePayload } else { $payload }
    $stageFlavor = if ($stage -eq "alternate") { $alternateFlavor } else { $Flavor }
    Copy-Item -Path (Join-Path $sourcePayload "*") -Destination $stagePayload -Recurse
    $payloadGeneration = if ($stage -eq "previous") { "previous" } else { "current" }
    Set-Content -LiteralPath (Join-Path $stagePayload "lifecycle-payload.txt") -Value $payloadGeneration -Encoding utf8
    Set-Content -LiteralPath (Join-Path $stagePayload "lifecycle-$payloadGeneration-only.txt") -Value $payloadGeneration -Encoding utf8

    foreach ($file in @("S3Explorer.Installer.wixproj", "Package.wxs", "packages.lock.json")) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "installer\$file") -Destination $projectRoot
    }
    $packagePath = Join-Path $projectRoot "Package.wxs"
    if ($stage -in @("previous", "legacy-incompatible")) {
        Set-Content -LiteralPath $packagePath -Value (Read-LegacySource -Path "installer/Package.wxs") -NoNewline -Encoding utf8
    }
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
        "-p:InstallerFlavor=$stageFlavor", "-p:InstallerOutputName=lifecycle-$stage",
        "-p:ApplicationIconPath=$(Join-Path $repositoryRoot 'src\S3Explorer.App\Assets\S3Explorer.ico')",
        "-p:OutputPath=$output"
    )
    $packages[$stage] = (Resolve-Path -LiteralPath (Join-Path $output "lifecycle-$stage.msi")).Path
}

# A deliberately failing, tiny MSI verifies msiexec's actual parser before any
# transaction or file installation, including drive-root paths that must not be populated.
$probeRoot = Join-Path $fixtureRoot "argument-probe"
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
foreach ($file in @("S3Explorer.Installer.wixproj", "packages.lock.json")) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "installer\$file") -Destination $probeRoot
}
@'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="S3 Explorer argument probe" Manufacturer="project-base-mirror" Version="0.0.1"
      Scope="perMachine" UpgradeCode="1BB9A31E-B764-46D7-BD01-1186C0C59A8B">
    <Property Id="APPLICATIONFOLDER" Secure="yes" />
    <MediaTemplate EmbedCab="yes" />
    <CustomAction Id="ReportMsiArgument" Error="S3EXPLORER_MSI_ARGUMENT_PROBE:[APPLICATIONFOLDER]:END" />
    <InstallExecuteSequence><Custom Action="ReportMsiArgument" Before="CostInitialize" Condition="1" /></InstallExecuteSequence>
    <StandardDirectory Id="TempFolder">
      <Component Id="NeverInstalled" Guid="*">
        <RegistryValue Root="HKCU" Key="Software\S3ExplorerArgumentProbe" Name="NeverInstalled" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </StandardDirectory>
    <Feature Id="Probe" Level="1"><ComponentRef Id="NeverInstalled" /></Feature>
  </Package>
</Wix>
'@ | Set-Content -LiteralPath (Join-Path $probeRoot "Package.wxs") -Encoding utf8
Invoke-DotNet -Arguments @(
    "build", (Join-Path $probeRoot "S3Explorer.Installer.wixproj"), "-c", $Configuration,
    "-p:RestoreLockedMode=true", "-p:Version=0.0.1", "-p:InstallerOutputName=argument-probe", "-p:OutputPath=$probeRoot\package"
)

[ordered]@{
    schemaVersion = 1
    flavor = $Flavor
    previousMsi = $packages.previous
    currentMsi = $packages.current
    rollbackMsi = $packages.rollback
    alternateMsi = $packages.alternate
    legacyIncompatibleMsi = $packages.'legacy-incompatible'
    legacyUpdater = (Join-Path $legacyUpdaterPayload "S3Explorer.Updater.exe")
    legacySourceCommits = $legacyCommits
    legacyUpdaterSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $legacyProjectRoot "Program.cs") -Algorithm SHA256).Hash
    argumentProbeMsi = (Join-Path $probeRoot "package\argument-probe.msi")
    packageSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot "installer\Package.wxs") -Algorithm SHA256).Hash
    scope = "Current application payload; synthetic MSI versions 0.0.1 and 0.0.2. Previous and incompatible MSI authoring plus legacy updater are rebuilt from pinned v0.8.8/v0.8.9 source. This validates historical updater compatibility, not byte-identical historical release packages."
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot "fixtures.json") -Encoding utf8
Write-Host "Test-only installer fixtures: $fixtureRoot"
