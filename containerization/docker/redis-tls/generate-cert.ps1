# Generates a fresh self-signed cert+key pair for the containerized Redis's TLS listener.
#
# Deliberately NOT committed to source control (redis.crt/redis.key are gitignored) -- run this
# once per deployment/release before building images, then plug the printed thumbprint into
# whichever environment you're deploying to (Bicep's redisTrustedCertificateThumbprint parameter,
# or the matching Terraform variable in each of azure/aws/local). FHIRBridge.Infrastructure's
# DependencyInjection.cs only trusts a self-signed Redis certificate whose thumbprint matches that
# configured value (see ValidateRedisServerCertificate) -- it fails closed otherwise, so a missed
# or mismatched thumbprint means the app refuses to start, not a silently-insecure connection.
#
# Usage:
#   .\generate-cert.ps1                  # generates (or reports the existing) redis.crt/redis.key
#   .\generate-cert.ps1 -Force            # regenerates even if a cert+key pair already exists
#   $thumb = .\generate-cert.ps1 -Quiet   # for scripting: suppresses all Write-Host output and
#                                         # returns ONLY the thumbprint on the success stream, so
#                                         # it's directly capturable (Write-Host output below is
#                                         # NOT capturable via a pipe/variable-assignment at all -
#                                         # it bypasses the success stream entirely; that's why this
#                                         # flag exists rather than expecting callers to parse it)
param(
    [switch]$Force,
    [switch]$Quiet
)

function Write-Info([string]$Message, [string]$ForegroundColor = $null) {
    if ($Quiet) { return }
    if ($ForegroundColor) { Write-Host $Message -ForegroundColor $ForegroundColor } else { Write-Host $Message }
}

$ErrorActionPreference = "Stop"
$Here = $PSScriptRoot
$CrtPath = Join-Path $Here "redis.crt"
$KeyPath = Join-Path $Here "redis.key"

# openssl isn't reliably on PATH in a native PowerShell session even when Git for Windows is
# installed (Git Bash puts its own mingw64/bin on PATH; a plain PowerShell/pwsh session usually
# doesn't) - fall back to Git for Windows' own bundled copy before giving up, since that's the
# most common place it actually exists on a Windows dev machine.
$OpenSsl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $OpenSsl) {
    $fallbackCandidates = @(
        "$env:ProgramFiles\Git\mingw64\bin\openssl.exe",
        "${env:ProgramFiles(x86)}\Git\mingw64\bin\openssl.exe",
        "$env:ProgramFiles\Git\usr\bin\openssl.exe"
    )
    $OpenSsl = $fallbackCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $OpenSsl) {
    Write-Error "openssl not found on PATH or in Git for Windows' usual install locations. Install Git for Windows (bundles openssl) or OpenSSL directly, then re-run - or run this from Git Bash instead of PowerShell."
    exit 1
}

if ((Test-Path $CrtPath) -and (Test-Path $KeyPath) -and -not $Force) {
    Write-Info "redis.crt/redis.key already exist here - leaving them as-is (pass -Force to regenerate)."
} else {
    Write-Info "==> Generating a new self-signed certificate for Redis TLS ..."
    & $OpenSsl req -x509 -newkey rsa:2048 -nodes `
        -keyout $KeyPath -out $CrtPath `
        -days 3650 `
        -subj "/CN=fhirbridge-redis/O=FHIRBridge/OU=containerization" `
        -addext "subjectAltName=DNS:redis,DNS:*.internal,DNS:localhost"
    if ($LASTEXITCODE -ne 0) { throw "openssl req failed" }
}

$thumbprint = (& $OpenSsl x509 -in $CrtPath -noout -fingerprint -sha1) -replace '^.*=', '' -replace ':', ''
Write-Info ""
Write-Info "Certificate thumbprint (SHA-1): $thumbprint" "Cyan"
Write-Info ""
Write-Info "Set this as:"
Write-Info "  - Bicep:                redisTrustedCertificateThumbprint parameter"
Write-Info "  - Terraform (azure/aws/local): redis_trusted_certificate_thumbprint variable"
Write-Info ""
Write-Info "Then build/push images as usual (containerization/scripts/build-images.ps1|sh) -"
Write-Info "the fhirbridge-redis image bakes in whatever redis.crt/redis.key currently sit in this folder."

# The one line on the actual success stream (capturable via $x = & generate-cert.ps1 -Quiet, or
# even without -Quiet since Write-Host output above never reaches this stream anyway) - always
# emitted, since this is the actual useful return value regardless of -Quiet.
return $thumbprint
