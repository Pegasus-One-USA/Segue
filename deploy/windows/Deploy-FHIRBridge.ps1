<#
.SYNOPSIS
    Deploys a published FHIRBridge build (4 Windows Services + 2 static frontend folders) to this
    Windows VM.

.DESCRIPTION
    Invoked by the self-hosted GitHub Actions runner (see .github/workflows/deploy.yml) after the
    build job's artifact has been downloaded and extracted. For each Windows Service (Api, Gateway,
    Worker, DemoApi): stops it (if running), mirrors the new published files into the deploy path,
    overlays that app's hand-provisioned config files from ConfigRoot (appsettings.Production.json
    etc. -- never part of the artifact, provisioned once by hand on this VM and never touched by the
    mirror step since it lives in a separate folder tree), then (re)creates and starts the service.
    For each static frontend (Portal, DemoPortal): mirrors the built files and overlays its config
    folder the same way -- no service involved, since Gateway and DemoApi serve these themselves
    (see deploy/windows/README.md).

.PARAMETER ArtifactPath
    Path to the extracted publish artifact. Must contain Api\, Gateway\, Worker\, DemoApi\, Portal\,
    and DemoPortal\ subfolders.

.PARAMETER DeployRoot
    Root folder on this VM under which every app's deploy folder lives. Defaults to
    C:\inetpub\wwwroot, matching the folder names already in use on this server
    (fhirbridge-api, fhirbridge-gateway, fhirbridge-portal, fhirbridge-worker, demoapp-api,
    demoapp-portal) even though none of them are IIS-hosted.

.PARAMETER ConfigRoot
    Root folder on this VM holding each app's hand-provisioned config files, mirrored under the same
    per-app folder names as DeployRoot (e.g. ConfigRoot\fhirbridge-api\appsettings.Production.json).
    Never touched by CI -- ops edits files here directly. After every deploy's file mirror, this
    app's ConfigRoot subfolder is copied on top of its DeployRoot subfolder, so config always
    survives a redeploy without needing robocopy /XF exclusions. Defaults to
    C:\inetpub\FHIRBridge_Configurations.

.PARAMETER ApiPort, GatewayPort, DemoApiPort
    The port each service's Kestrel instance binds to. Mandatory and with no baked-in default on
    purpose: these used to be configured once, by hand, via each Windows Service's Environment tab
    in services.msc, and silently drifted back to Kestrel's own built-in default (port 5000) whenever
    that step was skipped or a service was recreated -- which then collides with whatever else is
    listening on 5000 on this host. Every deploy now sets ASPNETCORE_URLS explicitly from these
    parameters instead. Wire the actual values in via this repo's GitHub Actions repository/environment
    variables (see .github/workflows/deploy.yml) so ops can change a port without a code change.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [string]$DeployRoot = "C:\inetpub\wwwroot",

    [string]$ConfigRoot = "C:\inetpub\FHIRBridge_Configurations",

    [Parameter(Mandatory = $true)]
    [int]$ApiPort,

    [Parameter(Mandatory = $true)]
    [int]$GatewayPort,

    [Parameter(Mandatory = $true)]
    [int]$DemoApiPort,

    [int]$ServiceStopTimeoutSeconds = 30,

    # Separate from ServiceStopTimeoutSeconds on purpose: BootstrapDatabase (Program.cs) runs
    # dbContext.Database.Migrate() synchronously before the service reports Running, and a migration
    # batch that alters/indexes a large existing table (e.g. terminology.TRM_CONCEPT, populated by the
    # Hapi*TerminologySyncService workers) can take well over 30 seconds one-time. Reusing the 30s stop
    # timeout for this wait made the very first deploy after such a migration appear to fail here even
    # though the process was still up and finishing the migration -- a redeploy then "worked" only
    # because the migration had already completed. 180s gives one-time heavy migrations headroom without
    # masking a genuinely hung/crashed service for multiple minutes.
    [int]$ServiceStartTimeoutSeconds = 180,

    [int]$HealthCheckRetries = 10,
    [int]$HealthCheckDelaySeconds = 3
)

$ErrorActionPreference = "Stop"

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

    # /E copies all files/subfolders (including empty ones) without /MIR's delete-extras behavior --
    # this only ever adds/overwrites files already present in DestDir from the artifact mirror above,
    # never removes anything. ConfigRoot is the source of truth for these files, never touched by CI.
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

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping service $Name..."
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
    }

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    # /MIR mirrors source into dest (adds new files, removes ones no longer published). Config files
    # (appsettings.Production.json etc.) are never part of the artifact and live entirely under
    # ConfigRoot instead, so they're unaffected by this mirror -- overlaid back on afterward below.
    # Exit codes 0-7 are robocopy's normal "success" range; >=8 is a real failure.
    robocopy $SourceDir $DestDir /MIR /XD "logs" /NFL /NDL /NP /R:3 /W:5
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed deploying $Name (exit code $LASTEXITCODE)"
    }
    # robocopy's exit codes 0-7 are all "success" variants (2 = extras purged, etc.), but the value stays
    # non-zero in $LASTEXITCODE regardless -- clear it so it can't leak into this script's own final exit
    # code once everything below finishes normally.
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
        (Get-Service -Name $Name).WaitForStatus('Running', (New-TimeSpan -Seconds $ServiceStartTimeoutSeconds))
    } catch {
        Write-Host "Service $Name failed to start -- capturing diagnostics before failing the deploy."
        Show-ServiceStartFailureDiagnostics -ExePath $exePath -EnvironmentVariables $EnvironmentVariables
        throw
    }
    Write-Host "$Name is running."
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

function Test-Health {
    param(
        [string]$Name,
        [string]$Url
    )

    Write-Host "Health-checking $Name at $Url..."
    # Gateway/DemoApp's cert is issued for the public hostname (e.g. segue.pegasusone.com), not
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

# --- Static frontends first (no service -- served by Gateway / DemoApi respectively). These must
# land on disk BEFORE Gateway/DemoApi start: both only wire up their static-file middleware if the
# configured folder already exists at process startup (see Gateway/Program.cs and
# Demo_TestApp/backend/Program.cs) -- deploying them after the service starts would leave the portal
# unserved until the next restart. ---

$staticSites = @(
    @{ Name = "Portal"; Folder = "Portal"; DestDir = "fhirbridge-portal" }
    @{ Name = "DemoPortal"; Folder = "DemoPortal"; DestDir = "demoapp-portal" }
)

foreach ($site in $staticSites) {
    $sourceDir = Join-Path $ArtifactPath $site.Folder
    if (-not (Test-Path $sourceDir)) { throw "Artifact is missing $($site.Folder)\ folder at $sourceDir" }

    Deploy-StaticFiles -Name $site.Name -SourceDir $sourceDir -DestDir (Join-Path $DeployRoot $site.DestDir) `
        -ConfigSourceDir (Join-Path $ConfigRoot $site.DestDir)
}

# --- Windows Services (Kestrel/background hosts) ---

$demoappPortalDir = Join-Path $DeployRoot "demoapp-portal"

$services = @(
    @{ Name = "FHIRBridge.Api"; Folder = "Api"; DestDir = "fhirbridge-api"; Exe = "FHIRBridge.Api.exe";
       DisplayName = "FHIRBridge API"; HealthCheckUrl = "http://127.0.0.1:$ApiPort/health";
       EnvironmentVariables = @("ASPNETCORE_URLS=http://127.0.0.1:$ApiPort") }
    @{ Name = "FHIRBridge.Gateway"; Folder = "Gateway"; DestDir = "fhirbridge-gateway"; Exe = "FHIRBridge.Gateway.exe";
       DisplayName = "FHIRBridge Gateway"; HealthCheckUrl = "https://localhost:$GatewayPort/";
       EnvironmentVariables = @("ASPNETCORE_URLS=https://+:$GatewayPort") }
    @{ Name = "FHIRBridge.Worker"; Folder = "Worker"; DestDir = "fhirbridge-worker"; Exe = "FHIRBridge.Worker.exe";
       DisplayName = "FHIRBridge Worker"; HealthCheckUrl = $null; EnvironmentVariables = @() }
    @{ Name = "FHIRBridge.DemoApp"; Folder = "DemoApi"; DestDir = "demoapp-api"; Exe = "HealthAppBackend.exe";
       DisplayName = "FHIRBridge Demo App"; HealthCheckUrl = "https://localhost:$DemoApiPort/";
       EnvironmentVariables = @("ASPNETCORE_URLS=https://+:$DemoApiPort", "DEMOAPP_PORTAL_PATH=$demoappPortalDir") }
)

foreach ($svc in $services) {
    $sourceDir = Join-Path $ArtifactPath $svc.Folder
    if (-not (Test-Path $sourceDir)) { throw "Artifact is missing $($svc.Folder)\ folder at $sourceDir" }

    Deploy-Service -Name $svc.Name -SourceDir $sourceDir -DestDir (Join-Path $DeployRoot $svc.DestDir) `
        -ConfigSourceDir (Join-Path $ConfigRoot $svc.DestDir) -ExeName $svc.Exe -DisplayName $svc.DisplayName `
        -EnvironmentVariables $svc.EnvironmentVariables
}

# --- Health checks (after every service is already started above) ---

foreach ($svc in $services) {
    if ($svc.HealthCheckUrl) {
        Test-Health -Name $svc.Name -Url $svc.HealthCheckUrl
    }
}

Write-Host "Deploy complete."
