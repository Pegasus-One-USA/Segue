<#
  Reset FHIRBridge to the first-run setup screen (dev).
  Removes all users (keeps roles/permissions) so requiresSetup -> true.
  Usage:  ./scripts/reset-to-first-setup.ps1
          ./scripts/reset-to-first-setup.ps1 -SaPassword 'xxx' -Container 'fhirbridge-controlplane-sql'
  Then open http://localhost:4200 in a fresh/incognito window.
#>
param(
  [string]$Container  = "fhirbridge-controlplane-sql",
  [string]$SaPassword = "Your_password123",
  [string]$Database   = "FHIRBridge"
)

$sql = "SET NOCOUNT ON; DELETE FROM [UserRoles]; DELETE FROM [Users]; SELECT 'Users remaining = ' + CAST(COUNT(*) AS varchar) FROM [Users];"

& docker exec $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $SaPassword -C -d $Database -Q $sql
if ($LASTEXITCODE -ne 0) { Write-Error "Reset failed (is the '$Container' container running?)"; exit 1 }

Write-Host ""
Write-Host "Reset complete. Open http://localhost:4200 in a fresh/incognito window to hit the first-run setup screen." -ForegroundColor Green
