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
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactPath,

    [string]$DeployRoot = "C:\inetpub\wwwroot",

    [string]$ConfigRoot = "C:\inetpub\FHIRBridge_Configurations",

    [int]$ServiceStopTimeoutSeconds = 30,
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

function Deploy-Service {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$DestDir,
        [string]$ConfigSourceDir,
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

    Write-Host "Starting service $Name..."
    Start-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus('Running', (New-TimeSpan -Seconds $ServiceStopTimeoutSeconds))
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

$services = @(
    @{ Name = "FHIRBridge.Api"; Folder = "Api"; DestDir = "fhirbridge-api"; Exe = "FHIRBridge.Api.exe";
       DisplayName = "FHIRBridge API"; HealthCheckUrl = "http://127.0.0.1:5000/health" }
    @{ Name = "FHIRBridge.Gateway"; Folder = "Gateway"; DestDir = "fhirbridge-gateway"; Exe = "FHIRBridge.Gateway.exe";
       DisplayName = "FHIRBridge Gateway"; HealthCheckUrl = "http://localhost/" }
    @{ Name = "FHIRBridge.Worker"; Folder = "Worker"; DestDir = "fhirbridge-worker"; Exe = "FHIRBridge.Worker.exe";
       DisplayName = "FHIRBridge Worker"; HealthCheckUrl = $null }
    @{ Name = "FHIRBridge.DemoApp"; Folder = "DemoApi"; DestDir = "demoapp-api"; Exe = "HealthAppBackend.exe";
       DisplayName = "FHIRBridge Demo App"; HealthCheckUrl = "http://localhost:5500/" }
)

foreach ($svc in $services) {
    $sourceDir = Join-Path $ArtifactPath $svc.Folder
    if (-not (Test-Path $sourceDir)) { throw "Artifact is missing $($svc.Folder)\ folder at $sourceDir" }

    Deploy-Service -Name $svc.Name -SourceDir $sourceDir -DestDir (Join-Path $DeployRoot $svc.DestDir) `
        -ConfigSourceDir (Join-Path $ConfigRoot $svc.DestDir) -ExeName $svc.Exe -DisplayName $svc.DisplayName
}

# --- Health checks (after every service is already started above) ---

foreach ($svc in $services) {
    if ($svc.HealthCheckUrl) {
        Test-Health -Name $svc.Name -Url $svc.HealthCheckUrl
    }
}

Write-Host "Deploy complete."
