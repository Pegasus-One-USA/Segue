<#
.SYNOPSIS
    Deploys a published FHIRBridge build (Api + Gateway + Worker + Portal) to this Windows VM.

.DESCRIPTION
    Invoked by the self-hosted GitHub Actions runner (see .github/workflows/deploy.yml) after the
    build job's artifact has been downloaded and extracted. Matches the Kestrel-native / YARP
    Gateway architecture documented in Documents/Kestrel-Gateway-Deployment-Setup.html:
    FHIRBridge.Gateway is the only public-facing process (binds 80/443, proxies /api/** and
    /swagger/** to FHIRBridge.Api on loopback, serves the Portal's static build directly).
    FHIRBridge.Api and FHIRBridge.Worker run as internal-only Windows Services.

    For each service (Api/Gateway/Worker): stops it if running, mirrors the new published files
    into place via robocopy while preserving appsettings.Production.json (never part of the
    artifact — provisioned once by hand on this VM, see README.md), creates the service on first
    run, then starts it. The Portal is mirrored as plain static files (no service).

.PARAMETER ArtifactPath
    Path to the extracted publish artifact. Must contain Api\, Gateway\, Worker\, and Portal\ subfolders.

.PARAMETER DeployRoot
    Root folder on this VM under which fhirbridge-api\, fhirbridge-gateway\, fhirbridge-worker\,
    and fhirbridge-portal\ live. Defaults to C:\inetpub\wwwroot, matching the hand-validated layout.

.PARAMETER ApiHealthCheckUrl
    Loopback URL polled after the Api service starts. Pass '' to skip.

.PARAMETER GatewayHealthCheckUrl
    Plain-HTTP URL polled after the Gateway service starts, confirming the public entry point
    itself is reachable (avoids dealing with the self-signed HTTPS cert from this script). Pass
    '' to skip.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [string]$DeployRoot = "C:\inetpub\wwwroot",

    [string]$ApiServiceName = "FHIRBridge.Api",
    [string]$GatewayServiceName = "FHIRBridge.Gateway",
    [string]$WorkerServiceName = "FHIRBridge.Worker",

    [string]$ApiHealthCheckUrl = "http://127.0.0.1:5000/health",
    [string]$GatewayHealthCheckUrl = "http://localhost/",

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

function Deploy-StaticFiles {
    param(
        [string]$Label,
        [string]$SourceDir,
        [string]$DestDir
    )

    Write-Host "== Deploying $Label (static files) =="

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    robocopy $SourceDir $DestDir /MIR /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Label (exit code $LASTEXITCODE)"
    }

    Write-Host "$Label deployed."
}

function Test-HealthCheck {
    param(
        [string]$Label,
        [string]$Url
    )

    if (-not $Url) {
        return
    }

    Write-Host "Health-checking $Label at $Url..."
    for ($i = 1; $i -le $HealthCheckRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Host "$Label is healthy."
                return
            }
        } catch {
            Write-Host "Attempt $i/$HealthCheckRetries not healthy yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds $HealthCheckDelaySeconds
    }

    throw "$Label did not become healthy at $Url after $HealthCheckRetries attempts."
}

$apiSource = Join-Path $ArtifactPath "Api"
$gatewaySource = Join-Path $ArtifactPath "Gateway"
$workerSource = Join-Path $ArtifactPath "Worker"
$portalSource = Join-Path $ArtifactPath "Portal"

foreach ($required in @(
    @{ Name = "Api"; Path = $apiSource },
    @{ Name = "Gateway"; Path = $gatewaySource },
    @{ Name = "Worker"; Path = $workerSource },
    @{ Name = "Portal"; Path = $portalSource }
)) {
    if (-not (Test-Path $required.Path)) {
        throw "Artifact is missing the $($required.Name) folder at $($required.Path)"
    }
}

# Api and Worker first (internal-only, nothing depends on the Gateway being up), then the Portal's
# static files, then the Gateway last since it's the public entry point and proxies to the Api.
Deploy-Service -Name $ApiServiceName -SourceDir $apiSource -DestDir (Join-Path $DeployRoot "fhirbridge-api") `
    -ExeName "FHIRBridge.Api.exe" -DisplayName "FHIRBridge API"

Deploy-Service -Name $WorkerServiceName -SourceDir $workerSource -DestDir (Join-Path $DeployRoot "fhirbridge-worker") `
    -ExeName "FHIRBridge.Worker.exe" -DisplayName "FHIRBridge Worker"

Deploy-StaticFiles -Label "Portal" -SourceDir $portalSource -DestDir (Join-Path $DeployRoot "fhirbridge-portal")

Deploy-Service -Name $GatewayServiceName -SourceDir $gatewaySource -DestDir (Join-Path $DeployRoot "fhirbridge-gateway") `
    -ExeName "FHIRBridge.Gateway.exe" -DisplayName "FHIRBridge Gateway"

Test-HealthCheck -Label "Api" -Url $ApiHealthCheckUrl
Test-HealthCheck -Label "Gateway" -Url $GatewayHealthCheckUrl

Write-Host "Deploy complete."
