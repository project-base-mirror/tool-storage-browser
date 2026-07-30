[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [double]$MinimumLineRate = 0.60,
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\coverage\$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$projects = @(
    "tests\S3Explorer.Core.Tests\S3Explorer.Core.Tests.csproj",
    "tests\S3Explorer.Infrastructure.Cdn.Tests\S3Explorer.Infrastructure.Cdn.Tests.csproj"
)
foreach ($project in $projects) {
    & dotnet test (Join-Path $repositoryRoot $project) -c $Configuration --nologo --no-restore `
        --settings (Join-Path $repositoryRoot "eng\focused-coverage.runsettings") `
        --collect "XPlat Code Coverage" `
        --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage test failed for $project (exit code $LASTEXITCODE)."
    }
}

$reports = @(Get-ChildItem -Path $OutputDirectory -Recurse -Filter "coverage.cobertura.xml")
if ($reports.Count -ne $projects.Count) {
    throw "Expected $($projects.Count) coverage reports, found $($reports.Count)."
}

$groups = [ordered]@{
    persistence = "DurableJsonFile|JsonTransferTaskStore|JsonFolderSyncJobStore|JsonCdnJobStore|JsonCdnConfigurationStore|JsonCdnCredentialStore"
    transferQueue = "^S3Explorer\.Core\.PersistentTransferQueue$"
    syncPlanning = "^S3Explorer\.Core\.(FolderSyncPlanner|FolderSyncPlanSelection|FolderSyncPlanIdentity)$"
    cdnQueue = "^S3Explorer\.Core\.PersistentCdnJobQueue$"
}
$summary = [ordered]@{
    generatedAt = [DateTimeOffset]::UtcNow
    minimumLineRate = $MinimumLineRate
    groups = @()
}

foreach ($group in $groups.GetEnumerator()) {
    $lines = @{}
    foreach ($report in $reports) {
        [xml]$document = Get-Content -LiteralPath $report.FullName -Raw
        foreach ($class in @($document.coverage.packages.package.classes.class)) {
            if ([string]$class.name -notmatch $group.Value) { continue }
            foreach ($line in @($class.lines.line)) {
                $sourceName = [IO.Path]::GetFileName(([string]$class.filename).Replace('\', [IO.Path]::DirectorySeparatorChar))
                $key = "$sourceName`:$($line.number)"
                $hits = [int]$line.hits
                $lines[$key] = if ($lines.ContainsKey($key)) {
                    [Math]::Max([int]$lines[$key], $hits)
                } else {
                    $hits
                }
            }
        }
    }
    if ($lines.Count -eq 0) {
        throw "Coverage group '$($group.Key)' did not match any executable lines."
    }
    $covered = @($lines.Values | Where-Object { $_ -gt 0 }).Count
    $rate = $covered / $lines.Count
    $summary.groups += [pscustomobject][ordered]@{
        name = $group.Key
        coveredLines = $covered
        totalLines = $lines.Count
        lineRate = [Math]::Round($rate, 4)
        passed = $rate -ge $MinimumLineRate
    }
}

$summaryPath = Join-Path $OutputDirectory "focused-coverage.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary.groups | Format-Table name, coveredLines, totalLines, lineRate, passed -AutoSize
if (@($summary.groups | Where-Object { -not $_.passed }).Count -gt 0) {
    throw "Focused coverage gate failed. See $summaryPath"
}
Write-Host "Focused coverage gate passed: $summaryPath"
