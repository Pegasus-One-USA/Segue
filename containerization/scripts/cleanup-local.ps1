# Tears down the local Segue containerized stack — either the docker-compose path (default)
# or the Terraform local environment (-Terraform), so nothing keeps running/consuming disk after
# testing.
#
# Usage:
#   ./cleanup-local.ps1                  # docker compose down -v (containers + volumes + network)
#   ./cleanup-local.ps1 -KeepVolumes     # docker compose down, keep volumes (data survives)
#   ./cleanup-local.ps1 -Terraform       # terraform destroy in terraform/environments/local instead
#   ./cleanup-local.ps1 -Yes             # skip the confirmation prompt
param(
    [switch]$Terraform,
    [switch]$KeepVolumes,
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")

if (-not $Yes) {
    $reply = Read-Host "This removes every Segue local container (and volumes, unless -KeepVolumes) - continue? [y/N]"
    if ($reply -notmatch '^[Yy]$') { Write-Host "Aborted."; exit 1 }
}

if ($Terraform) {
    Write-Host "==> terraform destroy (terraform/environments/local)"
    Push-Location (Join-Path $RepoRoot "containerization/terraform/environments/local")
    try { terraform destroy -auto-approve } finally { Pop-Location }
} else {
    Push-Location (Join-Path $RepoRoot "containerization/compose")
    try {
        if ($KeepVolumes) {
            Write-Host "==> docker compose down"
            docker compose down
        } else {
            Write-Host "==> docker compose down -v"
            docker compose down -v
        }
    } finally { Pop-Location }
}

Write-Host "Done. Local Segue stack removed."
