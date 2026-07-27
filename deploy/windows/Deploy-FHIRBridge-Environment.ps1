<#
.SYNOPSIS
    Deploys a published FHIRBridge build to one of the non-production environments (Staging, QA,
    Dev, Working) on this Windows VM, alongside production. Sibling to Deploy-FHIRBridge.ps1 (which
    remains the production-only script, untouched by this file).

.DESCRIPTION
    Each environment gets its own 4-service topology under DeployRoot\<Environment>\ and
    ConfigRoot\<Environment>\, on its own set of ports, with its own Windows Service names
    (fhirbridge-<env>-*) -- the exact same shape production already uses, just parameterized per
    environment:
      - FHIRBridge.Gateway serves the portal (from a sibling "fhirbridge-portal" static folder) AND
        proxies /api/**, /swagger/** to this environment's own Api instance, all on ONE port.
      - FHIRBridge.Api is loopback-only, reached only through Gateway.
      - The Demo app backend (HealthAppBackend.exe) serves its own demo portal (from a sibling
        "demoapp-portal" static folder, via DEMOAPP_PORTAL_PATH) AND its own API, on ONE port.
      - FHIRBridge.Worker has no HTTP endpoint.
    "fhirbridge-portal" and "demoapp-portal" are plain static-file folders here, not services --
    mirrored first (same reasoning as production: Gateway/DemoApp only wire up static-file serving if
    the folder already exists when the process starts).

    Because the portal is same-origin with its own API here (exactly like production), the portal and
    Demo app frontend builds use the ordinary "production" Angular configuration -- no per-environment
    build or CORS configuration is needed.

.PARAMETER Environment
    Which non-production environment to deploy: Staging, QA, Dev, or Working. Selects the port table,
    Windows Service name prefix, and DeployRoot/ConfigRoot subfolder (matching casing) below.

.PARAMETER ArtifactPath
    Path to the extracted publish artifact. Must contain Api\, Gateway\, Worker\, DemoApi\, Portal\,
    and DemoPortal\ subfolders -- the same artifact shape production uses, built with the ordinary
    "production" Angular configuration for Portal\ and DemoPortal\.

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
# derived from anything. Only Api/Gateway/DemoApi/Worker ports are actually bindable; the portal and
# demoapp-portal ports from the original spec are retired now that both are served same-origin
# through Gateway/DemoApi respectively (see the deployment guide for why).
$EnvironmentTable = @{
    Staging = @{ Prefix = "staging"; Ports = @{ Api = 2002; Gateway = 2003; DemoApi = 2011; Worker = 2020 } }
    QA      = @{ Prefix = "qa";      Ports = @{ Api = 3002; Gateway = 3003; DemoApi = 3011; Worker = 3020 } }
    Dev     = @{ Prefix = "dev";     Ports = @{ Api = 4002; Gateway = 4003; DemoApi = 4011; Worker = 4020 } }
    Working = @{ Prefix = "working"; Ports = @{ Api = 6002; Gateway = 6003; DemoApi = 6011; Worker = 6020 } }
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

function Set-ServiceEnvironment {
    param([string]$Name, [string[]]$EnvironmentVariables)

    if (-not $EnvironmentVariables -or $EnvironmentVariables.Count -eq 0) {
        return
    }

    # Written directly to the service's registry key rather than via a services.msc-equivalent
    # cmdlet (none ship in-box) -- REG_MULTI_SZ needs an explicit string[], not the Object[] a bare
    # array literal would produce. Called every deploy (not just service creation) so ASPNETCORE_URLS
    # (and, for the demo app, DEMOAPP_PORTAL_PATH) stay correct even if ever changed here later -- the
    # caller already stopped the service before this runs, so the new value takes effect on the very
    # next Start-Service.
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name Environment `
        -Value ([string[]]$EnvironmentVariables) -Type MultiString
}

function Show-ServiceStartFailureDiagnostics {
    param([string]$ExePath, [string[]]$EnvironmentVariables)

    # Start-Service only ever reports a generic wrapper error (CouldNotStartService) -- it never
    # surfaces the app's real startup exception. This runs on the same VM as the app it just failed
    # to start, so there's no need to RDP in separately: check what's already on the configured
    # port, then run the exe directly (same env vars, same working directory) so whatever it prints
    # on the way down lands straight in this CI log.
    Write-Host "---- Diagnostics: why did this service fail to start? ----"

    $urlsVar = $EnvironmentVariables | Where-Object { $_ -like "ASPNETCORE_URLS=*" } | Select-Object -First 1
    if ($urlsVar -and ($urlsVar -match ':(\d+)(/|$)')) {
        $port = $matches[1]
        Write-Host "Configured to bind port $port -- existing listeners on that port:"
        try {
            $conns = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
        } catch {
            $conns = $null
        }
        if ($conns) {
            $conns | Select-Object LocalAddress, LocalPort, State, OwningProcess | Format-Table | Out-String | Write-Host
            $conns | Select-Object -Unique -ExpandProperty OwningProcess | ForEach-Object {
                Get-Process -Id $_ -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, Path | Format-List | Out-String | Write-Host
            }
        } else {
            Write-Host "(nothing else appears to be listening on port $port right now)"
        }
    }

    $originalEnv = @{}
    foreach ($kv in $EnvironmentVariables) {
        $idx = $kv.IndexOf('=')
        if ($idx -gt 0) {
            $key = $kv.Substring(0, $idx); $val = $kv.Substring($idx + 1)
            $originalEnv[$key] = [System.Environment]::GetEnvironmentVariable($key)
            [System.Environment]::SetEnvironmentVariable($key, $val)
        }
    }
    $stdOut = [System.IO.Path]::GetTempFileName()
    $stdErr = [System.IO.Path]::GetTempFileName()
    try {
        Write-Host "Running $ExePath directly for 5s to capture its own startup output..."
        $proc = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath -Parent) `
            -RedirectStandardOutput $stdOut -RedirectStandardError $stdErr -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 5
        if ($proc.HasExited) {
            Write-Host "It exited on its own with code $($proc.ExitCode) -- this is almost certainly the real failure."
        } else {
            Write-Host "Still running after 5s (didn't crash immediately) -- stopping the diagnostic run."
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    } finally {
        foreach ($key in $originalEnv.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $originalEnv[$key])
        }
    }
    Write-Host "---- stdout ----"
    Get-Content $stdOut -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    Write-Host "---- stderr ----"
    Get-Content $stdErr -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    Remove-Item $stdOut, $stdErr -ErrorAction SilentlyContinue

    Write-Host "---- Recent Application event log entries ----"
    Get-WinEvent -LogName Application -MaxEvents 20 -ErrorAction SilentlyContinue |
        Where-Object { $_.TimeCreated -gt (Get-Date).AddMinutes(-2) } |
        Select-Object TimeCreated, ProviderName, Id, Message | Format-List | Out-String | Write-Host
}

function Deploy-StaticFiles {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$DestDir,
        [string]$ConfigSourceDir
    )

    Write-Host "== Deploying static files: $Name =="

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    robocopy $SourceDir $DestDir /MIR /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0

    Copy-ConfigOverlay -Name $Name -ConfigSourceDir $ConfigSourceDir -DestDir $DestDir
    Write-Host "$Name deployed."
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

    robocopy $SourceDir $DestDir /MIR /XD "logs" /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name (exit code $LASTEXITCODE)"
    }
    $global:LASTEXITCODE = 0

    Copy-ConfigOverlay -Name $Name -ConfigSourceDir $ConfigSourceDir -DestDir $DestDir

    $exePath = Join-Path $DestDir $ExeName
    if (-not (Test-Path $exePath)) {
        throw "Expected executable not found after deploy: $exePath"
    }

    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        Write-Host "Service $Name does not exist yet -- creating it."
        New-Service -Name $Name -BinaryPathName "`"$exePath`"" -DisplayName $DisplayName -StartupType Automatic
    }

    Set-ServiceEnvironment -Name $Name -EnvironmentVariables $EnvironmentVariables

    Write-Host "Starting service $Name..."
    try {
        Start-Service -Name $Name
        (Get-Service -Name $Name).WaitForStatus('Running', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    } catch {
        Write-Host "Service $Name failed to start -- capturing diagnostics before failing the deploy."
        Show-ServiceStartFailureDiagnostics -ExePath $exePath -EnvironmentVariables $EnvironmentVariables
        throw
    }
    Write-Host "$Name is running."
}

function Test-Health {
    param([string]$Name, [string]$Url)

    Write-Host "Health-checking $Name at $Url..."
    # Gateway/DemoApi's cert is issued for the public hostname (e.g. segue.pegasusone.com), not
    # "localhost" -- this internal-only probe intentionally skips certificate validation since it's
    # just confirming the process is up and responding, not verifying the public TLS chain. Windows
    # PowerShell 5.1's Invoke-WebRequest has no -SkipCertificateCheck flag, so this goes through
    # ServicePointManager instead, scoped to this function and restored afterward either way.
    $originalCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    try {
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
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $originalCallback
    }
}

$prefix = $envInfo.Prefix
$ports = $envInfo.Ports
$fhirbridgePortalDir = Join-Path $envDeployRoot "fhirbridge-portal"
$demoappPortalDir = Join-Path $envDeployRoot "demoapp-portal"

# --- Static frontends first -- both Gateway and the Demo app backend only wire up static-file
# serving if their configured folder already exists when the process starts (see Gateway/Program.cs
# and Demo_TestApp/backend/Program.cs), same reasoning as production. ---

Deploy-StaticFiles -Name "fhirbridge-portal" -SourceDir (Join-Path $ArtifactPath "Portal") `
    -DestDir $fhirbridgePortalDir -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-portal")

Deploy-StaticFiles -Name "demoapp-portal" -SourceDir (Join-Path $ArtifactPath "DemoPortal") `
    -DestDir $demoappPortalDir -ConfigSourceDir (Join-Path $envConfigRoot "demoapp-portal")

$healthChecks = @()

# 1. fhirbridge-api -- loopback-only, matching production's own security posture. Reached only
# through Gateway's reverse proxy.
Deploy-Service -Name "fhirbridge-$prefix-api" `
    -SourceDir (Join-Path $ArtifactPath "Api") -DestDir (Join-Path $envDeployRoot "fhirbridge-api") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-api") -ExeName "FHIRBridge.Api.exe" `
    -DisplayName "FHIRBridge $Environment API" -EnvironmentVariables @("ASPNETCORE_URLS=http://127.0.0.1:$($ports.Api)")
$healthChecks += @{ Name = "fhirbridge-$prefix-api"; Url = "http://127.0.0.1:$($ports.Api)/health" }

# 2. fhirbridge-gateway -- serves the portal (StaticFiles:RootPath in this instance's config must
# point at $fhirbridgePortalDir) AND proxies /api/**, /swagger/** to this environment's own Api
# instance, on the SAME port. Health-checked via "/" (the portal's own index.html), exactly like
# production -- no extra health-route config needed here.
Deploy-Service -Name "fhirbridge-$prefix-gateway" `
    -SourceDir (Join-Path $ArtifactPath "Gateway") -DestDir (Join-Path $envDeployRoot "fhirbridge-gateway") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-gateway") -ExeName "FHIRBridge.Gateway.exe" `
    -DisplayName "FHIRBridge $Environment Gateway" -EnvironmentVariables @("ASPNETCORE_URLS=https://+:$($ports.Gateway)")
$healthChecks += @{ Name = "fhirbridge-$prefix-gateway"; Url = "https://localhost:$($ports.Gateway)/" }

# 3. demoapp-api -- serves its own demo portal (via DEMOAPP_PORTAL_PATH, set here automatically to
# $demoappPortalDir) AND its own API, on the SAME port -- same-origin, exactly like production.
Deploy-Service -Name "fhirbridge-$prefix-demoapp-api" `
    -SourceDir (Join-Path $ArtifactPath "DemoApi") -DestDir (Join-Path $envDeployRoot "demoapp-api") `
    -ConfigSourceDir (Join-Path $envConfigRoot "demoapp-api") -ExeName "HealthAppBackend.exe" `
    -DisplayName "FHIRBridge $Environment Demo App API" `
    -EnvironmentVariables @("ASPNETCORE_URLS=https://+:$($ports.DemoApi)", "DEMOAPP_PORTAL_PATH=$demoappPortalDir")
$healthChecks += @{ Name = "fhirbridge-$prefix-demoapp-api"; Url = "https://localhost:$($ports.DemoApi)/" }

# 4. fhirbridge-worker -- Host.CreateApplicationBuilder, no Kestrel/HTTP endpoint at all (see
# src/Worker/FHIRBridge.Worker/Program.cs). ASPNETCORE_URLS would be inert here, so none is set, and
# there's no health check URL.
Deploy-Service -Name "fhirbridge-$prefix-worker" `
    -SourceDir (Join-Path $ArtifactPath "Worker") -DestDir (Join-Path $envDeployRoot "fhirbridge-worker") `
    -ConfigSourceDir (Join-Path $envConfigRoot "fhirbridge-worker") -ExeName "FHIRBridge.Worker.exe" `
    -DisplayName "FHIRBridge $Environment Worker"

# --- Health checks (after every service is already started above) ---
foreach ($check in $healthChecks) {
    Test-Health -Name $check.Name -Url $check.Url
}

Write-Host "$Environment deploy complete."
