# Builds the 3 custom images, brings up the containerized product stack (containerization/compose),
# and checks every public site responds - a one-command "build it and prove it works" loop meant to
# be rerun after every build.
#
# Usage:
#   ./smoke-test.ps1                      # build (local tag) + up + check + leave stack running
#   ./smoke-test.ps1 -Tag v1.0.1          # build/run a specific tag
#   ./smoke-test.ps1 -SkipBuild           # reuse already-built images, just (re)start + check
#   ./smoke-test.ps1 -Down                # tear the stack down again after checks pass/fail
#   ./smoke-test.ps1 -TimeoutSeconds 300  # allow longer for slow first-boot / DB migration
param(
    [string]$Tag = "local",
    [switch]$SkipBuild,
    [switch]$Down,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$ComposeDir = Join-Path $RepoRoot "containerization/compose"

if (-not (Test-Path (Join-Path $ComposeDir ".env"))) {
    throw "Missing containerization/compose/.env - copy .env.example to .env and fill in secrets first."
}

if (-not $SkipBuild) {
    Write-Host "==> Building images (tag: $Tag)"
    & (Join-Path $PSScriptRoot "build-images.ps1") -Tag $Tag
}

# Read host ports out of .env so this still works if someone overrode the defaults there.
function Get-EnvValue([string]$Name, [string]$Default) {
    $line = Get-Content (Join-Path $ComposeDir ".env") | Where-Object { $_ -match "^\s*$Name\s*=" } | Select-Object -Last 1
    if ($line) { return ($line -split "=", 2)[1].Trim() }
    return $Default
}
$AppPort = Get-EnvValue "APP_HOST_PORT" "8080"

Write-Host "==> Starting stack (docker compose up -d)"
$env:IMAGE_TAG = $Tag
Push-Location $ComposeDir
try {
    docker compose up -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
} finally { Pop-Location }

$Sites = @(
    @{ Name = "segue-app portal";  Url = "http://localhost:$AppPort/" }
    @{ Name = "segue-app swagger"; Url = "http://localhost:$AppPort/swagger/index.html" }
)

Write-Host "==> Waiting for sites to respond (timeout: ${TimeoutSeconds}s each)"
$Results = @()
foreach ($site in $Sites) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ok = $false
    $lastError = ""
    while ((Get-Date) -lt $deadline) {
        try {
            $resp = Invoke-WebRequest -Uri $site.Url -UseBasicParsing -TimeoutSec 10
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 400) { $ok = $true; break }
        } catch { $lastError = $_.Exception.Message }
        Start-Sleep -Seconds 5
    }
    $Results += [pscustomobject]@{ Site = $site.Name; Url = $site.Url; Status = if ($ok) { "UP" } else { "FAILED" }; Detail = if ($ok) { "" } else { $lastError } }
}

Write-Host ""
Write-Host "==> Results"
$Results | Format-Table -AutoSize

if ($Down) {
    Write-Host "==> Tearing stack down (docker compose down)"
    Push-Location $ComposeDir
    try { docker compose down } finally { Pop-Location }
}

if ($Results | Where-Object { $_.Status -ne "UP" }) {
    Write-Host "One or more sites failed to come up." -ForegroundColor Red
    exit 1
}

Write-Host "All sites are up." -ForegroundColor Green
