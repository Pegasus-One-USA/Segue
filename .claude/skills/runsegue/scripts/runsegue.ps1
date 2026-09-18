<#
.SYNOPSIS
  Stop, clean-build and run the local FHIRBridge stack (API + Worker + Angular portal)
  against a local containerised database (PostgreSQL by default, SQL Server optional).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1 -Prompt
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1 -DatabaseName FHIRBridge_Scratch
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1 -Provider SqlServer
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1 -NoClean
  powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1 -StopOnly
#>
[CmdletBinding()]
param(
    # Database engine. PostgreSql -> controlplane-postgres (5434); SqlServer -> controlplane-sql (1433).
    [ValidateSet('PostgreSql', 'SqlServer')]
    [string] $Provider = 'PostgreSql',

    # Local database name. Defaults to the active local dev database.
    [string] $DatabaseName = 'FHIRBridge_v2',

    # Skip the interactive Provider / DatabaseName prompts and use the values above.
    # The script asks by default so the wrong database is never used by accident.
    [switch] $NoPrompt,

    [string] $DbUser,
    [string] $DbPassword,
    [int]    $DbPort,

    [switch] $SkipPortal,
    [switch] $SkipWorker,
    [switch] $NoClean,
    [switch] $StopOnly,
    [int]    $ApiPort    = 5000,
    [int]    $PortalPort = 4200,
    [int]    $HealthTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

# --- paths -----------------------------------------------------------------
$RepoRoot      = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$SolutionPath  = Join-Path $RepoRoot 'FHIRBridge.sln'
$ApiProject    = Join-Path $RepoRoot 'src\Api\FHIRBridge.Api'
$WorkerProject = Join-Path $RepoRoot 'src\Worker\FHIRBridge.Worker'
$PortalDir     = Join-Path $RepoRoot 'portal'
$LogDir        = Join-Path $RepoRoot 'logs\runsegue'

if (-not (Test-Path $SolutionPath)) {
    throw "Could not locate FHIRBridge.sln at '$SolutionPath'. Run this script from inside the repo."
}

function Write-Step  ([string] $m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Info  ([string] $m) { Write-Host "    $m" }
function Write-Ok    ([string] $m) { Write-Host "    [ok] $m"   -ForegroundColor Green }
function Write-Warn2 ([string] $m) { Write-Host "    [warn] $m" -ForegroundColor Yellow }
function Write-Err2  ([string] $m) { Write-Host "    [fail] $m" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# 0. Resolve the database target (provider + name), optionally interactively
# ---------------------------------------------------------------------------
if (-not $NoPrompt -and -not $StopOnly) {
    Write-Step 'Database target'

    $providerInput = Read-Host "Provider - PostgreSql or SqlServer (default: $Provider)"
    if (-not [string]::IsNullOrWhiteSpace($providerInput)) {
        $p = $providerInput.Trim()
        if ($p -match '^(?i)(postgres(ql)?|pg)$')          { $Provider = 'PostgreSql' }
        elseif ($p -match '^(?i)(sql ?server|mssql|sql)$') { $Provider = 'SqlServer' }
        else { throw "Unrecognized provider '$p'. Use PostgreSql or SqlServer." }
    }

    $dbInput = Read-Host "Database name (default: $DatabaseName)"
    if (-not [string]::IsNullOrWhiteSpace($dbInput)) { $DatabaseName = $dbInput.Trim() }
}

# Provider-specific defaults, matching docker-compose.yml.
if ($Provider -eq 'PostgreSql') {
    # NOTE: two Postgres containers exist locally. 'fhirbridge-controlplane-pg' is the one
    # actually published on host port 5434 and holding the dev data; the compose-defined
    # 'fhirbridge-controlplane-postgres' does not publish 5434. Target the former, and fall
    # back to the compose service name only if it is absent.
    $DbContainer = 'fhirbridge-controlplane-pg'
    $DbService   = 'controlplane-postgres'
    if (-not $DbPort)     { $DbPort = 5434 }
    if (-not $DbUser)     { $DbUser = 'postgres' }
    # The container enforces SCRAM-SHA-256, so a password is required over TCP. The committed
    # appsettings.Development.json omits it and only works via container-local trust auth;
    # connecting from the host without it fails with "No password has been provided".
    if (-not $DbPassword) { $DbPassword = 'Your_password123' }
    $ConnectionString = "Host=localhost;Port=$DbPort;Database=$DatabaseName;Username=$DbUser;Password=$DbPassword;"
} else {
    $DbContainer = 'fhirbridge-controlplane-sql'
    $DbService   = 'controlplane-sql'
    if (-not $DbPort)     { $DbPort = 1433 }
    if (-not $DbUser)     { $DbUser = 'sa' }
    if (-not $DbPassword) { $DbPassword = 'Your_password123' }
    $ConnectionString = "Server=localhost,$DbPort;Database=$DatabaseName;User Id=$DbUser;Password=$DbPassword;TrustServerCertificate=True;Encrypt=True"
}

Write-Info "provider : $Provider"
Write-Info "database : $DatabaseName (localhost:$DbPort)"

# ---------------------------------------------------------------------------
# 1. Stop anything already running
# ---------------------------------------------------------------------------
function Stop-ByName {
    param([string[]] $Names)
    foreach ($name in $Names) {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                Write-Info "killed $name (pid $($p.Id))"
            } catch {
                Write-Warn2 "could not kill $name (pid $($p.Id)): $($_.Exception.Message)"
            }
        }
    }
}

function Stop-AppHosts {
    # Only target the app's own hosts and an ng serve for THIS repo's portal.
    # Deliberately narrow: matching every process whose command line merely mentions
    # the repo path would also match the editor/agent tooling running from this
    # directory and kill the session that launched the script.
    $portalEscaped = [regex]::Escape($PortalDir)
    foreach ($exe in @('dotnet', 'node')) {
        foreach ($proc in @(Get-CimInstance Win32_Process -Filter "Name = '$exe.exe'" -ErrorAction SilentlyContinue)) {
            if ($proc.ProcessId -eq $PID) { continue }
            $cmd = $proc.CommandLine
            if ([string]::IsNullOrEmpty($cmd)) { continue }

            $isAppHost = $cmd -match 'FHIRBridge\.(Api|Worker)(\.dll|\.exe)?\b'
            $isNgServe = ($cmd -match $portalEscaped) -and ($cmd -match '\bng\b|angular') -and ($cmd -match '\bserve\b')

            if ($isAppHost -or $isNgServe) {
                try {
                    Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
                    Write-Info "killed $exe (pid $($proc.ProcessId))"
                } catch {
                    Write-Warn2 "could not kill $exe pid $($proc.ProcessId): $($_.Exception.Message)"
                }
            }
        }
    }
}

function Get-PortOwners {
    param([int] $Port)
    try {
        return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    } catch {
        return @()
    }
}

function Stop-ByPort {
    param([int] $Port)
    foreach ($c in (Get-PortOwners -Port $Port)) {
        if ($c.OwningProcess -eq 0 -or $c.OwningProcess -eq $PID) { continue }
        try {
            $owner = (Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue).ProcessName
            Stop-Process -Id $c.OwningProcess -Force -ErrorAction Stop
            Write-Info "freed port $Port (pid $($c.OwningProcess)$(if ($owner) { ", $owner" }))"
        } catch {
            Write-Warn2 "port $Port still held by pid $($c.OwningProcess): $($_.Exception.Message)"
        }
    }
}

function Test-PortFree {
    param([int] $Port)
    return ((Get-PortOwners -Port $Port).Count -eq 0)
}

Write-Step 'Stopping running API / Worker / portal'
Stop-ByName -Names @('FHIRBridge.Api', 'FHIRBridge.Worker')
Stop-AppHosts
Stop-ByPort -Port $ApiPort
Stop-ByPort -Port $PortalPort

$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    if ((Test-PortFree -Port $ApiPort) -and (Test-PortFree -Port $PortalPort)) { break }
    Start-Sleep -Milliseconds 500
}
if (Test-PortFree -Port $ApiPort)    { Write-Ok "port $ApiPort free" }    else { Write-Warn2 "port $ApiPort is still in use" }
if (Test-PortFree -Port $PortalPort) { Write-Ok "port $PortalPort free" } else { Write-Warn2 "port $PortalPort is still in use" }

if ($StopOnly) {
    Write-Host "`nStopped. Nothing launched (-StopOnly)." -ForegroundColor Cyan
    exit 0
}

# ---------------------------------------------------------------------------
# 2. Ensure the database container is up and the database exists
# ---------------------------------------------------------------------------
Write-Step "Verifying $Provider container ($DbContainer, port $DbPort)"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'docker is not on PATH. Start Docker Desktop and retry.'
}

$runningNames = @(& docker ps --format '{{.Names}}' 2>$null)
if ($runningNames -notcontains $DbContainer) {
    # Preferred container absent - fall back to the compose-defined one if it is already up.
    $fallback = if ($Provider -eq 'PostgreSql') { 'fhirbridge-controlplane-postgres' } else { 'fhirbridge-controlplane-sql' }
    if ($runningNames -contains $fallback) {
        Write-Warn2 "container '$DbContainer' not running - falling back to '$fallback'"
        $DbContainer = $fallback
    }
}

if ($runningNames -notcontains $DbContainer) {
    Write-Info 'container not running - starting via docker compose'
    Push-Location $RepoRoot
    try {
        & docker compose up -d $DbService
        if ($LASTEXITCODE -ne 0) { throw "docker compose up -d $DbService failed (exit $LASTEXITCODE)" }
    } finally {
        Pop-Location
    }
}

$dbReady  = $false
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    if ($Provider -eq 'PostgreSql') {
        & docker exec $DbContainer pg_isready -U $DbUser *> $null
    } else {
        & docker exec $DbContainer /opt/mssql-tools18/bin/sqlcmd -S localhost -U $DbUser -P $DbPassword -C -Q 'SELECT 1' *> $null
    }
    if ($LASTEXITCODE -eq 0) { $dbReady = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $dbReady) { throw "$Provider container '$DbContainer' did not become ready within 90s." }
Write-Ok "$Provider ready on localhost:$DbPort"

# The API's startup migration creates the schema, not the database itself.
if ($Provider -eq 'PostgreSql') {
    # Match case-insensitively: an unquoted CREATE DATABASE folds the name to lower case,
    # so an exact datname= comparison can miss a database that really does exist.
    $exists = ((& docker exec $DbContainer psql -U $DbUser -tAc "SELECT 1 FROM pg_database WHERE datname ILIKE '$DatabaseName'" 2>$null) -join '').Trim()
    if ($exists -ne '1') {
        Write-Info "database '$DatabaseName' does not exist - creating it"
        # Double-quote the identifier so mixed-case names are preserved verbatim.
        $createSql = 'CREATE DATABASE "' + $DatabaseName + '"'
        $createOut = & docker exec $DbContainer psql -U $DbUser -c $createSql 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Failed to create database '$DatabaseName': $createOut" }
    }
} else {
    & docker exec $DbContainer /opt/mssql-tools18/bin/sqlcmd -S localhost -U $DbUser -P $DbPassword -C `
        -Q "IF DB_ID('$DatabaseName') IS NULL CREATE DATABASE [$DatabaseName]" *> $null
    if ($LASTEXITCODE -ne 0) { throw "Failed to ensure database '$DatabaseName' exists." }
}
Write-Ok "database '$DatabaseName' present"

# ---------------------------------------------------------------------------
# 3. Clean
# ---------------------------------------------------------------------------
if ($NoClean) {
    Write-Step 'Clean skipped (-NoClean)'
} else {
    Write-Step 'Cleaning build output'
    foreach ($root in @((Join-Path $RepoRoot 'src'), (Join-Path $RepoRoot 'tests'))) {
        if (-not (Test-Path $root)) { continue }
        $dirs = @(Get-ChildItem -Path $root -Directory -Recurse -Force -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' })
        foreach ($d in $dirs) {
            try {
                Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Stop
            } catch {
                Write-Warn2 "could not remove $($d.FullName): $($_.Exception.Message)"
            }
        }
        Write-Info "removed $($dirs.Count) bin/obj folders under $(Split-Path $root -Leaf)"
    }
    $ngCache = Join-Path $PortalDir '.angular\cache'
    if (Test-Path $ngCache) {
        try {
            Remove-Item -LiteralPath $ngCache -Recurse -Force -ErrorAction Stop
            Write-Info 'removed portal/.angular/cache'
        } catch {
            Write-Warn2 "could not remove angular cache: $($_.Exception.Message)"
        }
    }
    Write-Ok 'clean complete'
}

# ---------------------------------------------------------------------------
# 4. Build
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

Write-Step 'Restoring and building the solution'
Push-Location $RepoRoot
try {
    & dotnet restore $SolutionPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

    # -p:UseAppHost=false: on machines where an EDR/security policy blocks writing new .exe files under
    # bin\Debug (observed on this dev box), MSBuild's apphost copy step fails with MSB3021 "Access to the
    # path ... FHIRBridge.Api.exe is denied" even though the directory is otherwise writable. Skipping the
    # native apphost avoids the write entirely; the hosts are launched via `dotnet <dll>` below anyway; see
    # Get-BuiltAssembly's .dll fallback.
    $buildLog = Join-Path $LogDir 'build.log'
    & dotnet build $SolutionPath -c Debug --no-restore -p:UseAppHost=false 2>&1 | Tee-Object -FilePath $buildLog
    if ($LASTEXITCODE -ne 0) {
        Write-Err2 "build failed - nothing was launched. See $buildLog"
        exit 1
    }
} finally {
    Pop-Location
}
Write-Ok 'build succeeded'

# ---------------------------------------------------------------------------
# 5. Portal dependencies
# ---------------------------------------------------------------------------
if (-not $SkipPortal) {
    $nodeModules = Join-Path $PortalDir 'node_modules'
    $lockFile    = Join-Path $PortalDir 'package-lock.json'
    $needsInstall = -not (Test-Path $nodeModules)
    if (-not $needsInstall -and (Test-Path $lockFile)) {
        $needsInstall = (Get-Item $lockFile).LastWriteTimeUtc -gt (Get-Item $nodeModules).LastWriteTimeUtc
    }
    if ($needsInstall) {
        Write-Step 'Installing portal dependencies (npm install)'
        Push-Location $PortalDir
        try {
            & npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed (exit $LASTEXITCODE)" }
        } finally {
            Pop-Location
        }
        Write-Ok 'npm install complete'
    } else {
        Write-Step 'Portal dependencies up to date (npm install skipped)'
    }
}

# ---------------------------------------------------------------------------
# 6. Launch
# ---------------------------------------------------------------------------
function Start-Background {
    param(
        [string]    $Name,
        [string]    $WorkingDirectory,
        [string]    $FilePath,
        [string]    $Arguments,
        [hashtable] $EnvironmentVars = @{}
    )
    $out = Join-Path $LogDir "$Name.log"
    $err = Join-Path $LogDir "$Name.err.log"
    foreach ($f in @($out, $err)) { if (Test-Path $f) { Remove-Item -LiteralPath $f -Force } }

    $saved = @{}
    foreach ($k in $EnvironmentVars.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $EnvironmentVars[$k])
    }
    try {
        # -ArgumentList rejects an empty string, so omit it entirely when there are no args.
        $common = @{
            FilePath               = $FilePath
            WorkingDirectory       = $WorkingDirectory
            RedirectStandardOutput = $out
            RedirectStandardError  = $err
            WindowStyle            = 'Hidden'
            PassThru               = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($Arguments)) { $common['ArgumentList'] = $Arguments }
        $p = Start-Process @common
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    Write-Info "$Name started (pid $($p.Id)) -> $out"
    return $p
}

# ConnectionStrings__FHIRBridgeDb / Database__Provider override appsettings at runtime,
# so the chosen database is used without editing any committed config file.
$HostEnv = @{
    'ASPNETCORE_ENVIRONMENT'           = 'Development'
    'DOTNET_ENVIRONMENT'               = 'Development'
    'ConnectionStrings__FHIRBridgeDb'  = $ConnectionString
    'Database__Provider'               = $Provider
}

$launched = @{}

function Get-BuiltAssembly {
    # Launch the built host directly instead of via `dotnet run`: `dotnet run` starts the real
    # host as a CHILD process and then exits, so the handle we hold reports "exited" while the
    # app is actually running - and killing that handle would leave the app orphaned on the port.
    param([string] $ProjectDir, [string] $AssemblyName)
    $binDir = Join-Path $ProjectDir 'bin\Debug'
    if (-not (Test-Path $binDir)) { return $null }
    foreach ($ext in @('exe', 'dll')) {
        $hit = Get-ChildItem -Path $binDir -Filter "$AssemblyName.$ext" -Recurse -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

Write-Step 'Launching API'
$apiAssembly = Get-BuiltAssembly -ProjectDir $ApiProject -AssemblyName 'FHIRBridge.Api'
if (-not $apiAssembly) { throw "Could not find a built FHIRBridge.Api under $ApiProject\bin\Debug." }
$apiDir = Split-Path $apiAssembly
if ($apiAssembly.EndsWith('.exe')) {
    $launched['api'] = Start-Background -Name 'api' -WorkingDirectory $apiDir `
        -FilePath $apiAssembly -Arguments "--urls http://localhost:$ApiPort" -EnvironmentVars $HostEnv
} else {
    $launched['api'] = Start-Background -Name 'api' -WorkingDirectory $apiDir `
        -FilePath 'dotnet' -Arguments "`"$apiAssembly`" --urls http://localhost:$ApiPort" -EnvironmentVars $HostEnv
}

if (-not $SkipWorker) {
    Write-Step 'Launching Worker'
    $workerAssembly = Get-BuiltAssembly -ProjectDir $WorkerProject -AssemblyName 'FHIRBridge.Worker'
    if (-not $workerAssembly) { throw "Could not find a built FHIRBridge.Worker under $WorkerProject\bin\Debug." }
    $workerDir = Split-Path $workerAssembly
    if ($workerAssembly.EndsWith('.exe')) {
        $launched['worker'] = Start-Background -Name 'worker' -WorkingDirectory $workerDir `
            -FilePath $workerAssembly -EnvironmentVars $HostEnv
    } else {
        $launched['worker'] = Start-Background -Name 'worker' -WorkingDirectory $workerDir `
            -FilePath 'dotnet' -Arguments "`"$workerAssembly`"" -EnvironmentVars $HostEnv
    }
}

if (-not $SkipPortal) {
    Write-Step 'Launching portal (ng serve)'
    $npmCmd  = Get-Command npm.cmd -ErrorAction SilentlyContinue
    $npmPath = if ($npmCmd) { $npmCmd.Source } else { 'npm.cmd' }
    $launched['portal'] = Start-Background -Name 'portal' -WorkingDirectory $PortalDir `
        -FilePath $npmPath -Arguments "start -- --port $PortalPort"
}

# ---------------------------------------------------------------------------
# 7. Health checks
# ---------------------------------------------------------------------------
function Wait-ForHttp {
    param(
        [string[]] $Urls,
        [int]      $TimeoutSeconds,
        [System.Diagnostics.Process] $Process
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) {
            # Re-read by id: a PassThru process object can report an empty ExitCode.
            $code = try { (Get-Process -Id $Process.Id -ErrorAction Stop).ExitCode } catch { $Process.ExitCode }
            if ($null -eq $code -or $code -eq '') { $code = 'unknown' }
            return "exited (code $code)"
        }
        foreach ($u in $Urls) {
            try {
                $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) { return 'ready' }
            } catch { }
        }
        Start-Sleep -Seconds 2
    }
    return 'timeout'
}

Write-Step 'Waiting for services to come up'

# Swagger first: locally /health reports 503 because the Azure Key Vault health check cannot resolve
# its probe secret, even though the API is serving requests normally. Readiness here means
# "answering HTTP", so a 503 from /health must not be read as a failed startup.
$apiState = Wait-ForHttp -Urls @("http://localhost:$ApiPort/swagger/index.html", "http://localhost:$ApiPort/health") `
                         -TimeoutSeconds $HealthTimeoutSeconds -Process $launched['api']
if ($apiState -eq 'ready') {
    Write-Ok "API responding on http://localhost:$ApiPort"
} else {
    Write-Err2 "API not healthy: $apiState (see $LogDir\api.log)"
}

$workerState = 'skipped'
if (-not $SkipWorker) {
    Start-Sleep -Seconds 3
    if ($launched['worker'].HasExited) {
        $workerState = "exited (code $($launched['worker'].ExitCode))"
        Write-Err2 "Worker $workerState (see $LogDir\worker.log)"
    } else {
        $workerState = 'running'
        Write-Ok 'Worker running'
    }
}

$portalState = 'skipped'
if (-not $SkipPortal) {
    $portalState = Wait-ForHttp -Urls @("http://localhost:$PortalPort") -TimeoutSeconds $HealthTimeoutSeconds -Process $launched['portal']
    if ($portalState -eq 'ready') {
        Write-Ok "Portal responding on http://localhost:$PortalPort"
    } else {
        Write-Err2 "Portal not healthy: $portalState (see $LogDir\portal.log)"
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host "`n=== runsegue summary ===" -ForegroundColor Cyan
$workerPid = if ($launched['worker']) { $launched['worker'].Id } else { '-' }
$portalPid = if ($launched['portal']) { $launched['portal'].Id } else { '-' }
@(
    [pscustomobject]@{ Service = 'API';    Status = $apiState;    ProcessId = $launched['api'].Id; Url = "http://localhost:$ApiPort/swagger" }
    [pscustomobject]@{ Service = 'Worker'; Status = $workerState; ProcessId = $workerPid;          Url = '-' }
    [pscustomobject]@{ Service = 'Portal'; Status = $portalState; ProcessId = $portalPid;          Url = "http://localhost:$PortalPort" }
) | Format-Table -AutoSize | Out-String | Write-Host

Write-Host "Database : $Provider localhost:$DbPort / $DatabaseName (container $DbContainer)"
Write-Host "Logs     : $LogDir"
Write-Host "Seq logs : http://localhost:5341"

$failed = @($apiState, $workerState, $portalState) | Where-Object { $_ -notin @('ready', 'running', 'skipped') }
if ($failed.Count -gt 0) { exit 1 }
exit 0
