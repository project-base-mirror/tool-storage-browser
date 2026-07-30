[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Endpoint = "",
    [string]$AccessKey = "",
    [string]$SecretKey = "",
    [string]$DockerImage = "quay.io/minio/minio@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e",
    [string]$ReportPath = "",
    [int]$StartupTimeoutSeconds = 120,
    [switch]$KeepContainer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$testProject = Join-Path $repositoryRoot "tests\S3Explorer.Infrastructure.S3.Tests\S3Explorer.Infrastructure.S3.Tests.csproj"
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repositoryRoot "artifacts\local-minio\result.json"
}

$containerName = "s3explorer-minio-test-$([Guid]::NewGuid().ToString('N'))"
$createdContainer = $false
$startedDockerDesktop = $false
$startedAt = [DateTimeOffset]::UtcNow
$previousEnvironment = @{
    S3EXPLORER_MATRIX_PROVIDER = $env:S3EXPLORER_MATRIX_PROVIDER
    S3EXPLORER_MINIO_ENDPOINT = $env:S3EXPLORER_MINIO_ENDPOINT
    S3EXPLORER_MINIO_ACCESS_KEY = $env:S3EXPLORER_MINIO_ACCESS_KEY
    S3EXPLORER_MINIO_SECRET_KEY = $env:S3EXPLORER_MINIO_SECRET_KEY
    S3EXPLORER_MINIO_REGION = $env:S3EXPLORER_MINIO_REGION
}

function Test-DockerReady {
    try {
        & docker info --format "{{.ServerVersion}}" 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [Parameter(Mandatory)][string]$FailureMessage,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

$result = [ordered]@{
    generatedAt = $startedAt
    completedAt = $null
    status = "Failed"
    endpoint = $Endpoint
    container = $null
    dockerImage = $null
    exitCode = $null
    message = $null
}

try {
    $providedValues = @(
        @($Endpoint, $AccessKey, $SecretKey) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($providedValues.Count -ne 0 -and $providedValues.Count -ne 3) {
        throw "Endpoint、AccessKey、SecretKey 必须同时提供，不能只提供其中一部分。"
    }

    if ($providedValues.Count -eq 0) {
        if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw "未找到 Docker CLI。请安装 Docker Desktop，或显式传入现有 MinIO 的 Endpoint、AccessKey、SecretKey。"
        }

        if (-not (Test-DockerReady)) {
            $dockerDesktop = Join-Path $env:ProgramFiles "Docker\Docker\Docker Desktop.exe"
            if (-not (Test-Path -LiteralPath $dockerDesktop)) {
                throw "Docker 服务未运行，并且未找到 Docker Desktop。"
            }

            Write-Host "Docker 服务未运行，正在启动 Docker Desktop..."
            Start-Process -FilePath $dockerDesktop -WindowStyle Hidden | Out-Null
            $startedDockerDesktop = $true
            Wait-Until -TimeoutSeconds $StartupTimeoutSeconds `
                -FailureMessage "等待 Docker Desktop 就绪超时。" `
                -Condition { Test-DockerReady }
        }

        $port = Get-FreeTcpPort
        $Endpoint = "http://127.0.0.1:$port"
        $AccessKey = "s3explorer-local"
        $SecretKey = "s3explorer-local-minio-secret"
        Write-Host "正在启动隔离的 MinIO 容器 $containerName ..."
        & docker run --detach --rm --name $containerName `
            --publish "127.0.0.1:${port}:9000" `
            --env "MINIO_ROOT_USER=$AccessKey" `
            --env "MINIO_ROOT_PASSWORD=$SecretKey" `
            $DockerImage server /data --console-address ":9001" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "MinIO 容器启动失败，docker exit code: $LASTEXITCODE。" }
        $createdContainer = $true
        $result.container = $containerName
        $result.dockerImage = $DockerImage
    }

    $result.endpoint = $Endpoint
    Wait-Until -TimeoutSeconds $StartupTimeoutSeconds `
        -FailureMessage "等待 MinIO 健康检查超时：$Endpoint/minio/health/ready" `
        -Condition {
            try {
                $response = Invoke-WebRequest -Uri "$Endpoint/minio/health/ready" -UseBasicParsing -TimeoutSec 3
                return $response.StatusCode -eq 200
            }
            catch {
                return $false
            }
        }

    $env:S3EXPLORER_MATRIX_PROVIDER = "minio"
    $env:S3EXPLORER_MINIO_ENDPOINT = $Endpoint
    $env:S3EXPLORER_MINIO_ACCESS_KEY = $AccessKey
    $env:S3EXPLORER_MINIO_SECRET_KEY = $SecretKey
    $env:S3EXPLORER_MINIO_REGION = "us-east-1"

    Write-Host "正在校验锁定依赖并执行真实 MinIO 集成测试（CRUD、队列暂停/取消、重启续传及远端变化）..."
    & dotnet restore $testProject --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "本地 MinIO 测试依赖还原失败，dotnet restore exit code: $LASTEXITCODE。"
    }
    & dotnet test $testProject -c $Configuration --nologo --no-restore `
        --filter "Category=Integration" `
        --logger "console;verbosity=normal"
    $result.exitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) {
        throw "本地 MinIO 集成测试失败，dotnet test exit code: $LASTEXITCODE。"
    }

    $result.status = "Passed"
    $result.message = "All configured local MinIO integration tests passed."
}
catch {
    $result.message = $_.Exception.Message
    throw
}
finally {
    $result.completedAt = [DateTimeOffset]::UtcNow
    $reportDirectory = Split-Path -Parent $ReportPath
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "本地 MinIO 测试报告：$ReportPath"

    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    if ($createdContainer -and -not $KeepContainer) {
        & docker rm --force $containerName 2>$null | Out-Null
    }

    if ($startedDockerDesktop) {
        Write-Host "Docker Desktop 由本脚本启动，将保持运行，便于后续本地测试。"
    }
}
