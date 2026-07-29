<#
  Full dev-DB reset of workflow / pipeline / connection data (dev only).
  Wipes workflows + run history, pipeline routes, mapping profiles, source
  connections, destination configurations, and related execution/launch logs.
  Keeps Users/Roles/Permissions/SystemSettings/NotificationSettings intact.
  See reset-workflow-data.sql for the exact table list and deletion order.

  Usage:  ./scripts/reset-workflow-data.ps1
          ./scripts/reset-workflow-data.ps1 -SaPassword 'xxx' -Container 'fhirbridge-controlplane-sql'

  Requires -Force to actually run the delete — without it, this only prints
  current row counts so you can see what would be wiped.
#>
param(
  [string]$Container  = "fhirbridge-controlplane-sql",
  [string]$SaPassword = "Your_password123",
  [string]$Database   = "FHIRBridge",
  [switch]$Force
)

$scriptPath = Join-Path $PSScriptRoot "reset-workflow-data.sql"
if (-not (Test-Path $scriptPath)) { Write-Error "Could not find $scriptPath"; exit 1 }

if (-not $Force) {
  Write-Host "Dry run (no -Force passed) - showing current row counts only, nothing will be deleted." -ForegroundColor Yellow
  $countSql = @"
SET NOCOUNT ON;
SELECT 'WorkflowDefinitions' AS TableName, COUNT(*) AS CurrentRows FROM [WorkflowDefinitions]
UNION ALL SELECT 'WorkflowRuns', COUNT(*) FROM [WorkflowRuns]
UNION ALL SELECT 'ResourcePipelineRoutes', COUNT(*) FROM [ResourcePipelineRoutes]
UNION ALL SELECT 'MappingProfiles', COUNT(*) FROM [MappingProfiles]
UNION ALL SELECT 'SourceConnections', COUNT(*) FROM [SourceConnections]
UNION ALL SELECT 'DestinationConfigurations', COUNT(*) FROM [DestinationConfigurations]
UNION ALL SELECT 'ConfiguredPipelineRuns', COUNT(*) FROM [ConfiguredPipelineRuns]
UNION ALL SELECT 'SmartLaunchLogs', COUNT(*) FROM [SmartLaunchLogs];
"@
  & docker exec $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $SaPassword -C -d $Database -Q $countSql
  if ($LASTEXITCODE -ne 0) { Write-Error "Row-count check failed (is the '$Container' container running?)"; exit 1 }
  Write-Host ""
  Write-Host "Re-run with -Force to actually delete this data." -ForegroundColor Yellow
  exit 0
}

Write-Host "Deleting all workflow/pipeline/connection data..." -ForegroundColor Cyan
Get-Content $scriptPath -Raw | docker exec -i $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $SaPassword -C -d $Database
if ($LASTEXITCODE -ne 0) { Write-Error "Reset failed (is the '$Container' container running?)"; exit 1 }

Write-Host ""
Write-Host "Reset complete. Restart the Api/Worker if either is running to clear any in-memory caches." -ForegroundColor Green
