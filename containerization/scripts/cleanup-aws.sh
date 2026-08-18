#!/usr/bin/env bash
# Tears down everything the AWS Terraform environment created — VPC, ECR repos, ECS cluster/tasks/
# services, EFS, Cloud Map namespace, ALB, Secrets Manager secrets, IAM role, CloudWatch log
# groups. `terraform destroy` removes all of it in dependency order in one pass.
#
# Usage:
#   ./cleanup-aws.sh          # prompts for confirmation, then terraform destroy
#   ./cleanup-aws.sh -y       # skip the confirmation prompt (-auto-approve)
set -euo pipefail

ASSUME_YES="false"
while getopts "y" opt; do
  case "$opt" in
    y) ASSUME_YES="true" ;;
    *) echo "Usage: $0 [-y]" >&2; exit 1 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}/containerization/terraform/environments/aws"

if [[ ! -f terraform.tfstate && ! -d .terraform ]]; then
  echo "No local Terraform state found in $(pwd) — nothing to destroy from here." >&2
  echo "(If state is stored remotely, run 'terraform init' first so destroy can find it.)" >&2
  exit 1
fi

if [[ "$ASSUME_YES" == "true" ]]; then
  terraform destroy -auto-approve
else
  terraform destroy
fi

echo "Done. AWS resources for this stack have been removed."
