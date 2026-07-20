<#
.SYNOPSIS
    Deploys a published FHIRBridge build to one of the non-production environments (Staging, QA,
    Dev, Working) on this Windows VM, alongside production. Sibling to Deploy-FHIRBridge.ps1 (which
    remains the production-only script, untouched by this file).

.DESCRIPTION
    Each environment gets its own 6-service topology under DeployRoot\<Environment>\ and
    ConfigRoot\<Environment>\, on its own set of ports, with its own Windows Service names
    (fhirbridge-<env>-*). Unlike production -- where Gateway serves the portal and proxies the API
    from the same origin/port -- these environments decouple the portal from the Gateway entirely, so
    each portal is independently reachable on its own port. This requires no new executable: the
    already-published FHIRBridge.Gateway.exe is simply deployed as three separate service instances
    per environment, configured differently via appsettings.Production.json alone:
      - "fhirbridge-portal"       -- static-only instance (StaticFiles:RootPath set, serves the built
                                     portal from its own wwwroot subfolder here)
      - "fhirbridge-gateway"      -- proxy-only instance (StaticFiles:RootPath left empty -- Gateway's
                                     own Program.cs already skips static serving and just logs a
                                     warning in that case -- ReverseProxy points at this environment's
                                     own Api instance)
      - "demoapp-portal"          -- static-only instance again, serving the demo app's portal build
    FHIRBridge.Api, FHIRBridge.Worker, and the Demo app backend (HealthAppBackend.exe) are each
    deployed once per environment, same shape as production.

    Because the portal is no longer same-origin with its API in these environments (different ports
    = different origins), each environment's portal/demo-portal Angular build must have been built
    with that environment's own API base URL baked in (see the "staging"/"qa"/"dev"/"working" Angular
    build configurations), and that origin must be present in the Api's Portal:AllowedOrigins CORS
    config for that environment's Api instance.

.PARAMETER Environment
    Which non-production environment to deploy: Staging, QA, Dev, or Working. Selects the port table,
    Windows Service name prefix, and DeployRoot/ConfigRoot subfolder (matching casing) below.

.PARAMETER ArtifactPath
    Path to the extracted publish artifact. Must contain Api\, Gateway\, Worker\, DemoApi\, Portal\,
    and DemoPortal\ subfolders -- the same artifact shape production uses. The Portal\ and DemoPortal\
    folders here must have been built with this Environment's own Angular configuration (see above),
    not the "production" one.

.PARAMETER DeployRoot
    Root folder on this VM under which every environment's deploy folder lives. Defaults to
    C:\inetpub\wwwroot (same root production uses, one level up -- production's own services live
    directly under this root; each non-prod environment gets its own <Environment> subfolder here so
    the two never collide).

.PARAMETER ConfigRoot
    Root folder on this VM holding every environment's hand-provisioned config files, mirroring
    DeployRoot's <Environment> subfolder structure. Defaults to
    C:\inetpub\FHIRBridge_Configurations (same root production uses).
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Staging", "QA", "Dev", "Working")]
    [string]$Environment,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [string]$DeployRoot = "C:\inetpub\wwwroot",

    [string]$ConfigRoot = "C:\inetpub\FHIRBridge_Configurations",

    [int]$ServiceStopTimeoutSeconds = 30,
    [int]$HealthCheckRetries = 10,
    [int]$HealthCheckDelaySeconds = 3
)

$ErrorActionPreference = "Stop"

# Port table + Windows Service name prefix per environment -- exact values as specified by ops, not
# derived from anything. Add a new environment here (plus a new trigger workflow) to extend this.
$EnvironmentTable = @{
    Staging = @{ Prefix = "staging"; Ports = @{ Portal = 2000; Api = 2002; Gateway = 2003; DemoPortal = 2010; DemoApi = 2011; Worker = 2020 } }
    QA      = @{ Prefix = "qa";      Ports = @{ Portal = 3000; Api = 3002; Gateway = 3003; DemoPortal = 3010; DemoApi = 3011; Worker = 3020 } }
    Dev     = @{ Prefix = "dev";     Ports = @{ Portal = 4000; Api = 4002; Gateway = 4003; DemoPortal = 4010; DemoApi = 4011; Worker = 4020 } }
    Working = @{ Prefix = "working"; Ports = @{ Portal = 6000; Api = 6002; Gateway = 6003; DemoPortal = 6010; DemoApi = 6011; Worker = 6020 } }
}
$envInfo = $EnvironmentTable[$Environment]
$envDeployRoot = Join-Path $DeployRoot $Environment
$envConfigRoot = Join-Path $ConfigRoot $Environment

function Copy-ConfigOverlay {
    param(
        [string]$Name,
        [string]$ConfigSourceDir,
        [string]$DestDir
    )

    if (-not (Test-Path $ConfigSourceDir)) {
        Write-Host "No config folder for $Name at $ConfigSourceDir -- skipping overlay."
        return
    }

    # /E copies all files/subfolders without /MIR's delete-extras behavior -- only ever adds/overwrites
    # files already present in DestDir from the mirror step above, never removes anything. ConfigRoot
    # is the source of truth for these files, never touched by CI.
    robocopy $ConfigSourceDir $DestDir /E /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed overlaying config for $Name (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0
}

function Stop-ServiceIfRunning {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping service $Name..."
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    }
}

function Set-ServiceEnvironment {
    param([string]$Name, [string[]]$EnvironmentVariables)

    if (-not $EnvironmentVariables -or $EnvironmentVariables.Count -eq 0) {
        return
    }

    # Written directly to the service's registry key rather than via a services.msc-equivalent
    # cmdlet (none ship in-box) -- REG_MULTI_SZ needs an explicit string[], not the Object[] a bare
    # array literal would produce. Called every deploy (not just service creation) so ASPNETCORE_URLS
    # stays correct even if it's ever changed here later -- the caller already stopped the service
    # before this runs, so the new value takes effect on the very next Start-Service.
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name Environment `
        -Value ([string[]]$EnvironmentVariables) -Type MultiString
}

function Start-AndWait {
    param([string]$Name, [string]$ExePath, [string]$DisplayName, [string[]]$EnvironmentVariables)

    if (-not (Test-Path $ExePath)) {
        throw "Expected executable not found after deploy: $ExePath"
    }

    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "Service $Name does not exist yet -- creating it."
        New-Service -Name $Name -BinaryPathName "`"$ExePath`"" -DisplayName $DisplayName -StartupType Automatic
    }

    Set-ServiceEnvironment -Name $Name -EnvironmentVariables $EnvironmentVariables

    Write-Host "Starting service $Name..."
    Start-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus('Running', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    Write-Host "$Name is running."
}

# Regular role: one published app (Api / Worker / DemoApi) mirrored as-is into its own service.
function Deploy-Service {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$DestDir,
        [string]$ConfigSourceDir,
        [string]$ExeName,
        [string]$DisplayName,
        [string[]]$EnvironmentVariables
    )

    Write-Host "== Deploying $Name =="
    Stop-ServiceIfRunning -Name $Name

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    robocopy $SourceDir $DestDir /MIR /XD "logs" "wwwroot" /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0

    Copy-ConfigOverlay -Name $Name -ConfigSourceDir $ConfigSourceDir -DestDir $DestDir
    Start-AndWait -Name $Name -ExePath (Join-Path $DestDir $ExeName) -DisplayName $DisplayName -EnvironmentVariables $EnvironmentVariables
}

# Static-portal role: an extra FHIRBridge.Gateway.exe instance, mirrored the same way as any other
# service, but with a built Angular site mirrored into its own wwwroot\ subfolder alongside it --
# this environment's ConfigRoot overlay for this service is expected to point
# StaticFiles:RootPath at that wwwroot folder (and leave ReverseProxy effectively unused).
function Deploy-StaticPortal {
    param(
        [string]$Name,
        [string]$GatewaySourceDir,
        [string]$ContentSourceDir,
        [string]$DestDir,
        [string]$ConfigSourceDir,
        [string]$DisplayName,
        [string[]]$EnvironmentVariables
    )

    Write-Host "== Deploying $Name (static portal) =="
    Stop-ServiceIfRunning -Name $Name

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    # /XD "wwwroot" keeps this mirror from deleting the content folder populated by the second
    # robocopy call below -- the Gateway artifact itself never contains a wwwroot, so this only ever
    # protects content this script placed there on an earlier run.
    robocopy $GatewaySourceDir $DestDir /MIR /XD "logs" "wwwroot" /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name binaries (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0

    $contentDest = Join-Path $DestDir "wwwroot"
    if (-not (Test-Path $contentDest)) {
        New-Item -ItemType Directory -Path $contentDest -Force | Out-Null
    }
    robocopy $ContentSourceDir $contentDest /MIR /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name content (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0

    Copy-ConfigOverlay -Name $Name -ConfigSourceDir $ConfigSourceDir -DestDir $DestDir
    Start-AndWait -Name $Name -ExePath (Join-Path $DestDir "FHIRBridge.Gateway.exe") -DisplayName $DisplayName -EnvironmentVariables $EnvironmentVariables
}

function Test-Health {
    param([string]$Name, [string]$Url)

    Write-Host "Health-checking $Name at $Url..."
    for ($i = 1; $i -le $HealthCheckRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Host "$Name is healthy."
                return
            }
        } catch {
            Write-Host "Attempt $i/$HealthCheckRetries not healthy yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds $HealthCheckDelaySeconds
    }

    throw "$Name did not become healthy at $Url after $HealthCheckRetries attempts."
}

$prefix = $envInfo.Prefix
$ports = $envInfo.Ports
$healthChecks = @()

# 1. fhirbridge-portal -- static Gateway instance serving the portal build on its own port.
Deploy-StaticPortal -Name "fhirbridge-$prefix-portal" `
    -GatewaySourceDir (Join-Path $ArtifactPath "Gateway") -ContentSourceDir (Join-Path $ArtifactPath "Portal") `
    -DestDir (Join-Path $envDeployRoot "fhirbridge-portal") -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-portal") `
    -DisplayName "FHIRBridge $Environment Portal" -EnvironmentVariables @("ASPNETCORE_URLS=http://+:$($ports.Portal)")
$healthChecks += @{ Name = "fhirbridge-$prefix-portal"; Url = "http://localhost:$($ports.Portal)/" }

# 2. fhirbridge-api -- loopback-only, matching production's own security posture.
Deploy-Service -Name "fhirbridge-$prefix-api" `
    -SourceDir (Join-Path $ArtifactPath "Api") -DestDir (Join-Path $envDeployRoot "fhirbridge-api") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-api") -ExeName "FHIRBridge.Api.exe" `
    -DisplayName "FHIRBridge $Environment API" -EnvironmentVariables @("ASPNETCORE_URLS=http://127.0.0.1:$($ports.Api)")
$healthChecks += @{ Name = "fhirbridge-$prefix-api"; Url = "http://127.0.0.1:$($ports.Api)/health" }

# 3. fhirbridge-gateway -- proxy-only Gateway instance (no StaticFiles:RootPath configured for this
# one). Health-checked via /health, which this instance's ConfigRoot overlay must add as its own
# YARP route to the api-cluster (see the deployment guide) -- Gateway has no /health of its own.
Deploy-Service -Name "fhirbridge-$prefix-gateway" `
    -SourceDir (Join-Path $ArtifactPath "Gateway") -DestDir (Join-Path $envDeployRoot "fhirbridge-gateway") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-gateway") -ExeName "FHIRBridge.Gateway.exe" `
    -DisplayName "FHIRBridge $Environment Gateway" -EnvironmentVariables @("ASPNETCORE_URLS=http://+:$($ports.Gateway)")
$healthChecks += @{ Name = "fhirbridge-$prefix-gateway"; Url = "http://localhost:$($ports.Gateway)/health" }

# 4. demoapp-portal -- static Gateway instance serving the demo app's portal build.
Deploy-StaticPortal -Name "fhirbridge-$prefix-demoapp-portal" `
    -GatewaySourceDir (Join-Path $ArtifactPath "Gateway") -ContentSourceDir (Join-Path $ArtifactPath "DemoPortal") `
    -DestDir (Join-Path $envDeployRoot "demoapp-portal") -ConfigSourceDir (Join-Path $envConfigRoot "demoapp-portal") `
    -DisplayName "FHIRBridge $Environment Demo Portal" -EnvironmentVariables @("ASPNETCORE_URLS=http://+:$($ports.DemoPortal)")
$healthChecks += @{ Name = "fhirbridge-$prefix-demoapp-portal"; Url = "http://localhost:$($ports.DemoPortal)/" }

# 5. demoapp-api -- health-checked via the existing public, unauthenticated /api/demo-types endpoint
# (Demo_TestApp/backend/Program.cs) rather than "/", since this instance no longer serves its own
# portal (root path has no static content to fall back to without demoapp-portal's folder here).
Deploy-Service -Name "fhirbridge-$prefix-demoapp-api" `
    -SourceDir (Join-Path $ArtifactPath "DemoApi") -DestDir (Join-Path $envDeployRoot "demoapp-api") `
    -ConfigSourceDir (Join-Path $envConfigRoot "demoapp-api") -ExeName "HealthAppBackend.exe" `
    -DisplayName "FHIRBridge $Environment Demo App API" -EnvironmentVariables @("ASPNETCORE_URLS=http://+:$($ports.DemoApi)")
$healthChecks += @{ Name = "fhirbridge-$prefix-demoapp-api"; Url = "http://localhost:$($ports.DemoApi)/api/demo-types" }

# 6. fhirbridge-worker -- Host.CreateApplicationBuilder, no Kestrel/HTTP endpoint at all (see
# src/Worker/FHIRBridge.Worker/Program.cs). Its port entry is reserved/unused; ASPNETCORE_URLS would
# be inert here, so none is set, and there's no health check URL.
Deploy-Service -Name "fhirbridge-$prefix-worker" `
    -SourceDir (Join-Path $ArtifactPath "Worker") -DestDir (Join-Path $envDeployRoot "fhirbridge-worker") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-worker") -ExeName "FHIRBridge.Worker.exe" `
    -DisplayName "FHIRBridge $Environment Worker"

# --- Health checks (after every service is already started above) ---
foreach ($check in $healthChecks) {
    Test-Health -Name $check.Name -Url $check.Url
}

Write-Host "$Environment deploy complete."
