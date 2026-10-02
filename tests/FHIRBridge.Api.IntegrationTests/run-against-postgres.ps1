<#
.SYNOPSIS
    Runs the API integration suite against a real PostgreSQL database instead of the in-memory stores.

.DESCRIPTION
    Creates an EMPTY throwaway database in the local PostgreSQL container, runs the suite with FHIRBRIDGE_IT_DB
    pointing at it (the API migrates it on startup; the fixture performs first-run setup), then drops it.
    The tests create and delete rows and need an empty install, so this never runs against a working database:
    the name must end in "_it".

.EXAMPLE
    ./run-against-postgres.ps1 -Password '<postgres password>'
    ./run-against-postgres.ps1 -Password '<postgres password>' -KeepDatabase   # keep it for inspection
#>
param(
    [Parameter(Mandatory = $true)] [string] $Password,
    [string] $Container = 'fhirbridge-controlplane-pg',
    [string] $HostName = 'localhost',
    [int]    $Port = 5434,
    [string] $User = 'postgres',
    [string] $Database = 'FHIRBridge_it',
    [string] $Filter,
    [switch] $KeepDatabase
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '_it$') {
    throw "Refusing to use '$Database': the suite needs an empty, throwaway database whose name ends in '_it'."
}

function Invoke-Psql([string] $Sql) {
    docker exec $Container psql -U $User -d postgres -v ON_ERROR_STOP=1 -q -c $Sql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $Sql" }
}

function Remove-TestDatabase {
    Invoke-Psql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();"
    Invoke-Psql "DROP DATABASE IF EXISTS `"$Database`";"
}

Write-Host "Creating empty database $Database in $Container..."
Remove-TestDatabase
Invoke-Psql "CREATE DATABASE `"$Database`";"

$env:FHIRBRIDGE_IT_DB = "Host=$HostName;Port=$Port;Database=$Database;Username=$User;Password=$Password"
if (-not $env:ASPNETCORE_URLS) { $env:ASPNETCORE_URLS = 'http://localhost:5099' }
try {
    $testArgs = @('test', (Join-Path $PSScriptRoot 'FHIRBridge.Api.IntegrationTests.csproj'), '--nologo')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:FHIRBRIDGE_IT_DB -ErrorAction SilentlyContinue
    if ($KeepDatabase) {
        Write-Host "Kept $Database for inspection."
    }
    else {
        Write-Host "Dropping $Database..."
        Remove-TestDatabase
    }
}

exit $exitCode
