<#
.SYNOPSIS
    Deploys a published FHIRBridge Api + Worker build to this Windows VM as Windows Services.

.DESCRIPTION
    Invoked by the self-hosted GitHub Actions runner (see .github/workflows/deploy.yml) after the
    build job's artifact has been downloaded and extracted. Stops each service (if running), mirrors
    the new published files into the deploy path while preserving appsettings.Production.json (which
    is never part of the artifact and must be provisioned once by hand on this VM), then (re)creates
    and starts the service.

.PARAMETER ArtifactPath
    Path to the extracted publish artifact. Must contain Api\ and Worker\ subfolders.

.PARAMETER DeployRoot
    Root folder on this VM under which Api\ and Worker\ live. Defaults to C:\FHIRBridge.

.PARAMETER HealthCheckUrl
    URL polled after the Api service starts to confirm it came up. Pass '' to skip.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [string]$DeployRoot = "C:\FHIRBridge",

    [string]$ApiServiceName = "FHIRBridge.Api",
    [string]$WorkerServiceName = "FHIRBridge.Worker",

    [string]$HealthCheckUrl = "http://localhost:5000/health",

    [int]$ServiceStopTimeoutSeconds = 30,
    [int]$HealthCheckRetries = 10,
    [int]$HealthCheckDelaySeconds = 3
)

$ErrorActionPreference = "Stop"

function Deploy-Service {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$DestDir,
        [string]$ExeName,
        [string]$DisplayName
    )

    Write-Host "== Deploying $Name =="

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping service $Name..."
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    }

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    # /MIR mirrors source into dest (adds new files, removes ones no longer published); /XF excludes
    # appsettings.Production.json from both copy AND delete so hand-provisioned secrets on this VM
    # survive every deploy untouched. Exit codes 0-7 are robocopy's normal "success" range; >=8 is a
    # real failure.
    robocopy $SourceDir $DestDir /MIR /XF "appsettings.Production.json" /XD "logs" /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name (exit code $LASTEXITCODE)"
    }

    $exePath = Join-Path $DestDir $ExeName
    if (-not (Test-Path $exePath)) {
        throw "Expected executable not found after deploy: $exePath"
    }

    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "Service $Name does not exist yet — creating it."
        New-Service -Name $Name -BinaryPathName "`"$exePath`"" -DisplayName $DisplayName -StartupType Automatic
    }

    Write-Host "Starting service $Name..."
    Start-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus('Running', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    Write-Host "$Name is running."
}

$apiSource = Join-Path $ArtifactPath "Api"
$workerSource = Join-Path $ArtifactPath "Worker"

if (-not (Test-Path $apiSource)) { throw "Artifact is missing Api\ folder at $apiSource" }
if (-not (Test-Path $workerSource)) { throw "Artifact is missing Worker\ folder at $workerSource" }

Deploy-Service -Name $ApiServiceName -SourceDir $apiSource -DestDir (Join-Path $DeployRoot "Api") `
    -ExeName "FHIRBridge.Api.exe" -DisplayName "FHIRBridge API"

Deploy-Service -Name $WorkerServiceName -SourceDir $workerSource -DestDir (Join-Path $DeployRoot "Worker") `
    -ExeName "FHIRBridge.Worker.exe" -DisplayName "FHIRBridge Worker"

if ($HealthCheckUrl) {
    Write-Host "Health-checking $HealthCheckUrl..."
    $healthy = $false
    for ($i = 1; $i -le $HealthCheckRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri $HealthCheckUrl -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        } catch {
            Write-Host "Attempt $i/$HealthCheckRetries not healthy yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds $HealthCheckDelaySeconds
    }

    if (-not $healthy) {
        throw "Api did not become healthy at $HealthCheckUrl after $HealthCheckRetries attempts."
    }
    Write-Host "Api is healthy."
}

Write-Host "Deploy complete."
