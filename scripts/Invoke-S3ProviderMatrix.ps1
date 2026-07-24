[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$ReportPath = "",
    [switch]$FailOnRequiredNotConfigured
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testProject = Join-Path $repositoryRoot "tests\S3Explorer.Infrastructure.S3.Tests\S3Explorer.Infrastructure.S3.Tests.csproj"
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repositoryRoot "artifacts\provider-matrix.json"
}

$providers = @(
    @{ id = "minio"; prefix = "S3EXPLORER_MINIO"; required = $true },
    @{ id = "aws"; prefix = "S3EXPLORER_AWS"; required = $false },
    @{ id = "tencent-cos"; prefix = "S3EXPLORER_TENCENT_COS"; required = $false },
    @{ id = "aliyun-oss"; prefix = "S3EXPLORER_ALIYUN_OSS"; required = $false },
    @{ id = "cloudflare-r2"; prefix = "S3EXPLORER_CLOUDFLARE_R2"; required = $false },
    @{ id = "backblaze-b2"; prefix = "S3EXPLORER_BACKBLAZE_B2"; required = $false },
    @{ id = "google-cloud-storage"; prefix = "S3EXPLORER_GCS"; required = $false },
    @{ id = "supabase-storage"; prefix = "S3EXPLORER_SUPABASE"; required = $false }
)

$results = [System.Collections.Generic.List[object]]::new()
$failed = $false
foreach ($provider in $providers) {
    $endpoint = [Environment]::GetEnvironmentVariable("$($provider.prefix)_ENDPOINT")
    $accessKey = [Environment]::GetEnvironmentVariable("$($provider.prefix)_ACCESS_KEY")
    $secretKey = [Environment]::GetEnvironmentVariable("$($provider.prefix)_SECRET_KEY")

    if ($provider.id -eq "minio") {
        if ([string]::IsNullOrWhiteSpace($endpoint)) { $endpoint = $env:S3EXPLORER_TEST_ENDPOINT }
        if ([string]::IsNullOrWhiteSpace($accessKey)) { $accessKey = $env:S3EXPLORER_TEST_ACCESS_KEY }
        if ([string]::IsNullOrWhiteSpace($secretKey)) { $secretKey = $env:S3EXPLORER_TEST_SECRET_KEY }
    }

    $configured =
        -not [string]::IsNullOrWhiteSpace($endpoint) -and
        -not [string]::IsNullOrWhiteSpace($accessKey) -and
        -not [string]::IsNullOrWhiteSpace($secretKey)

    if (-not $configured) {
        $results.Add([pscustomobject]@{
            provider = $provider.id
            required = $provider.required
            status = "NotConfigured"
            message = "Endpoint/access-key/secret-key are not all configured."
        })
        if ($provider.required -and $FailOnRequiredNotConfigured) { $failed = $true }
        continue
    }

    $previous = $env:S3EXPLORER_MATRIX_PROVIDER
    try {
        $env:S3EXPLORER_MATRIX_PROVIDER = $provider.id
        & dotnet test $testProject -c $Configuration --nologo `
            --filter "FullyQualifiedName~ProviderMatrixIntegrationTests" `
            --logger "console;verbosity=normal"
        if ($LASTEXITCODE -eq 0) {
            $results.Add([pscustomobject]@{
                provider = $provider.id
                required = $provider.required
                status = "Passed"
                message = "Configured provider matrix passed."
            })
        }
        else {
            $failed = $true
            $results.Add([pscustomobject]@{
                provider = $provider.id
                required = $provider.required
                status = "Failed"
                message = "dotnet test exited with code $LASTEXITCODE."
            })
        }
    }
    catch {
        $failed = $true
        $results.Add([pscustomobject]@{
            provider = $provider.id
            required = $provider.required
            status = "Failed"
            message = $_.Exception.GetType().Name
        })
    }
    finally {
        $env:S3EXPLORER_MATRIX_PROVIDER = $previous
    }
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$report = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow
    passed = @($results | Where-Object status -eq "Passed").Count
    failed = @($results | Where-Object status -eq "Failed").Count
    notConfigured = @($results | Where-Object status -eq "NotConfigured").Count
    skipped = @($results | Where-Object status -eq "Skipped").Count
    providers = $results
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$results | Format-Table provider, required, status, message -AutoSize
Write-Host "Provider matrix report: $((Resolve-Path -LiteralPath $ReportPath).Path)"

if ($failed) { exit 1 }
