<#
.SYNOPSIS
    Hard-deletes every resource in an Azure Health Data Services FHIR service, leaving the service
    instance itself, its configuration (SMART settings, CORS, private endpoints, RBAC) and audit
    logs untouched. This is the "purge resources via the FHIR API" approach, not the faster-but-
    destroys-config "delete and recreate the service" approach.

.DESCRIPTION
    The FHIR API has no bulk "wipe everything" operation. This script:
      1. Gets an Entra ID access token via client-credentials grant (same auth model FHIRBridge's
         own Azure FHIR Service destination uses - see FhirRepositoryAuthResolver).
      2. Discovers every resource type the service reports in its CapabilityStatement (or uses
         -ResourceTypes if you pass an explicit list).
      3. For each type with data, repeatedly pages the first $PageSize results and issues
         DELETE {type}/{id}?hardDelete=true for each one, until the type is empty.
      4. Re-counts every type at the end and warns about anything left over (failed deletes,
         insufficient permissions, etc).

    Prerequisites:
      - An Entra ID app registration (service principal) with a client secret.
      - That service principal must be assigned the "FHIR Data Contributor" role (or an equivalent
        custom role granting the hardDelete data action) on the target FHIR service - the built-in
        "FHIR Data Writer"/"FHIR Data Reader" roles are NOT enough to hard-delete.
      - If every delete in the run fails with HTTP 403, that's almost always this permission
        missing rather than a bug in the script.

.PARAMETER FhirServiceUrl
    e.g. https://myworkspace-myfhirservice.fhir.azurehealthcareapis.com

.PARAMETER Force
    Skip the interactive "type the hostname to confirm" prompt. Use for unattended/CI runs only.

.EXAMPLE
    $secret = Read-Host "Client secret" -AsSecureString
    ./clear-azure-fhir-service.ps1 `
        -FhirServiceUrl https://myworkspace-myfhirservice.fhir.azurehealthcareapis.com `
        -TenantId 72f9xxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx `
        -ClientId 0193c5xx-xxxx-xxxx-xxxx-xxxxxxxxxxxx `
        -ClientSecret $secret
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$FhirServiceUrl,
    [Parameter(Mandatory)] [string]$TenantId,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [securestring]$ClientSecret,
    [string]$Scope,
    [string[]]$ResourceTypes,
    [int]$PageSize = 100,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$FhirServiceUrl = $FhirServiceUrl.TrimEnd('/')
if (-not $Scope) { $Scope = "$FhirServiceUrl/.default" }

function Get-AccessToken {
    param([string]$TenantId, [string]$ClientId, [securestring]$ClientSecret, [string]$Scope)

    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecret)
    try {
        $plainSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $ClientId
        client_secret = $plainSecret
        scope         = $Scope
    }
    $tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    $resp = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -Body $body -ContentType 'application/x-www-form-urlencoded'
    return $resp.access_token
}

function Invoke-FhirWithRetry {
    param([string]$Method, [string]$Uri, [hashtable]$Headers)

    $attempt = 0
    while ($true) {
        try {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers
        } catch {
            $status = 0
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            $attempt++
            if (($status -eq 429 -or $status -eq 503) -and $attempt -le 5) {
                Start-Sleep -Seconds ([Math]::Min(30, [Math]::Pow(2, $attempt)))
                continue
            }
            throw
        }
    }
}

function Get-ResourceCount {
    param([string]$BaseUrl, [string]$Type, [hashtable]$Headers)
    try {
        $bundle = Invoke-FhirWithRetry -Method Get -Uri "$BaseUrl/$Type?_summary=count" -Headers $Headers
        return [int]$bundle.total
    } catch {
        Write-Warning "Could not get a count for $Type (HTTP $($_.Exception.Response.StatusCode)) - skipping it."
        return 0
    }
}

function Remove-AllOfType {
    param([string]$BaseUrl, [string]$Type, [hashtable]$Headers, [int]$PageSize)

    $deleted = 0
    while ($true) {
        $bundle = Invoke-FhirWithRetry -Method Get -Uri "$BaseUrl/$Type?_count=$PageSize&_elements=id" -Headers $Headers
        if (-not $bundle.entry -or $bundle.entry.Count -eq 0) { break }

        foreach ($entry in $bundle.entry) {
            $id = $entry.resource.id
            try {
                Invoke-FhirWithRetry -Method Delete -Uri "$BaseUrl/$Type/$id?hardDelete=true" -Headers $Headers | Out-Null
                $deleted++
            } catch {
                Write-Warning "Failed to hard-delete $Type/$id (HTTP $($_.Exception.Response.StatusCode)): $($_.Exception.Message)"
            }
        }
        Write-Host "  $Type`: deleted $deleted so far..."
    }
    return $deleted
}

$token = Get-AccessToken -TenantId $TenantId -ClientId $ClientId -ClientSecret $ClientSecret -Scope $Scope
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/fhir+json' }

if (-not $ResourceTypes) {
    Write-Host "Discovering resource types from $FhirServiceUrl/metadata..."
    $capabilityStatement = Invoke-FhirWithRetry -Method Get -Uri "$FhirServiceUrl/metadata" -Headers $headers
    $ResourceTypes = $capabilityStatement.rest[0].resource.type | Sort-Object -Unique
}
Write-Host "Checking $($ResourceTypes.Count) resource type(s) for data..."

$counts = @{}
foreach ($type in $ResourceTypes) { $counts[$type] = Get-ResourceCount -BaseUrl $FhirServiceUrl -Type $type -Headers $headers }

$nonEmpty = $counts.GetEnumerator() | Where-Object { $_.Value -gt 0 } | Sort-Object Name
$totalResources = ($counts.Values | Measure-Object -Sum).Sum

if ($totalResources -eq 0) {
    Write-Host "FHIR service already has no resources. Nothing to do."
    exit 0
}

Write-Host ""
Write-Host "About to permanently hard-delete $totalResources resource(s) across $($nonEmpty.Count) type(s) from:"
Write-Host "  $FhirServiceUrl"
$nonEmpty | ForEach-Object { Write-Host ("  {0,-30} {1}" -f $_.Name, $_.Value) }
Write-Host ""

if (-not $Force) {
    $expectedHost = ([uri]$FhirServiceUrl).Host
    $confirmation = Read-Host "This CANNOT be undone. Type the FHIR service host name ($expectedHost) to confirm"
    if ($confirmation -ne $expectedHost) {
        Write-Host "Confirmation did not match. Aborting - nothing was deleted."
        exit 1
    }
}

$grandTotal = 0
foreach ($item in $nonEmpty) {
    $type = $item.Name
    Write-Host "Purging $type ($($item.Value) resource(s))..."
    $deleted = Remove-AllOfType -BaseUrl $FhirServiceUrl -Type $type -Headers $headers -PageSize $PageSize
    Write-Host "  $type`: hard-deleted $deleted resource(s) total."
    $grandTotal += $deleted
}

Write-Host ""
Write-Host "Done. Hard-deleted $grandTotal resource(s) total."
Write-Host "Verifying..."

$remaining = 0
foreach ($type in $ResourceTypes) {
    $count = Get-ResourceCount -BaseUrl $FhirServiceUrl -Type $type -Headers $headers
    if ($count -gt 0) {
        Write-Warning "$type still has $count resource(s) remaining."
        $remaining += $count
    }
}

if ($remaining -eq 0) {
    Write-Host "Verified: FHIR service is empty."
} else {
    Write-Warning "$remaining resource(s) remain across one or more types. Re-run the script, or check the delete failure warnings above (HTTP 403 usually means the service principal is missing the hardDelete data action)."
}
