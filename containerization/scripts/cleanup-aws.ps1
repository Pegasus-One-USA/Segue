# Tears down everything the AWS Terraform environment created — VPC, ECR repos, ECS cluster/tasks/
# services, EFS, Cloud Map namespace, ALB, Secrets Manager secrets, IAM role, CloudWatch log
# groups. `terraform destroy` removes all of it in dependency order in one pass.
#
# Usage:
#   ./cleanup-aws.ps1          # prompts for confirmation, then terraform destroy
#   ./cleanup-aws.ps1 -Yes     # skip the confirmation prompt (-auto-approve)
param(
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$EnvDir = Join-Path $RepoRoot "containerization/terraform/environments/aws"

Push-Location $EnvDir
try {
    if (-not (Test-Path "terraform.tfstate") -and -not (Test-Path ".terraform")) {
        Write-Error "No local Terraform state found in $EnvDir - nothing to destroy from here. (If state is stored remotely, run 'terraform init' first.)"
        exit 1
    }

    if ($Yes) {
        terraform destroy -auto-approve
    } else {
        terraform destroy
    }
} finally {
    Pop-Location
}

Write-Host "Done. AWS resources for this stack have been removed."
