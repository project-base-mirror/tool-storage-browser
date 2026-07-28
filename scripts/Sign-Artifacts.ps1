[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [switch]$Require,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate
        }
    }

    throw "SignTool.exe was not found. Install the Windows SDK signing tools."
}

$certificateBase64 = [Environment]::GetEnvironmentVariable("S3EXPLORER_SIGNING_PFX_BASE64")
$certificatePassword = [Environment]::GetEnvironmentVariable("S3EXPLORER_SIGNING_PFX_PASSWORD")
$hasCertificate = -not [string]::IsNullOrWhiteSpace($certificateBase64)
$hasPassword = -not [string]::IsNullOrWhiteSpace($certificatePassword)

if ($hasCertificate -ne $hasPassword) {
    throw "Code signing configuration is incomplete; both PFX base64 and password are required."
}
if (-not $hasCertificate) {
    if ($Require) {
        throw "Code signing is required, but no trusted PFX certificate was configured."
    }
    Write-Host "Code signing is not configured; release artifacts remain unsigned."
    return
}

$resolvedPaths = @($Path | ForEach-Object {
    $resolved = (Resolve-Path -LiteralPath $_).Path
    if ([IO.Path]::GetExtension($resolved) -notin @(".exe", ".msi")) {
        throw "Only EXE and MSI release artifacts can be signed: $resolved"
    }
    $resolved
})
$signTool = Find-SignTool
$temporaryCertificate = Join-Path ([IO.Path]::GetTempPath()) ("s3explorer-sign-{0:N}.pfx" -f [Guid]::NewGuid())
$importedCertificate = $null
$certificateWasAlreadyInstalled = $false

try {
    [IO.File]::WriteAllBytes($temporaryCertificate, [Convert]::FromBase64String($certificateBase64))
    $certificateProbe = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $temporaryCertificate,
        $certificatePassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        $certificateWasAlreadyInstalled = Test-Path -LiteralPath "Cert:\CurrentUser\My\$($certificateProbe.Thumbprint)"
    }
    finally {
        $certificateProbe.Dispose()
    }
    $securePassword = ConvertTo-SecureString -String $certificatePassword -AsPlainText -Force
    $importedCertificate = Import-PfxCertificate `
        -FilePath $temporaryCertificate `
        -CertStoreLocation Cert:\CurrentUser\My `
        -Password $securePassword
    if ($null -eq $importedCertificate) {
        throw "The signing certificate could not be imported."
    }

    foreach ($artifact in $resolvedPaths) {
        Write-Host "Signing $artifact"
        & $signTool sign /fd SHA256 /sha1 $importedCertificate.Thumbprint /tr $TimestampUrl /td SHA256 $artifact
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed to sign $artifact with exit code $LASTEXITCODE."
        }
        & $signTool verify /pa /all $artifact
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool could not verify $artifact with exit code $LASTEXITCODE."
        }
    }
}
finally {
    if ($null -ne $importedCertificate -and -not $certificateWasAlreadyInstalled) {
        $certificatePath = "Cert:\CurrentUser\My\$($importedCertificate.Thumbprint)"
        if (Test-Path -LiteralPath $certificatePath) {
            Remove-Item -LiteralPath $certificatePath -Force
        }
    }
    if (Test-Path -LiteralPath $temporaryCertificate) {
        Remove-Item -LiteralPath $temporaryCertificate -Force
    }
}
