# Deploys the same 5-container topology to AWS ECS Fargate. Terraform only deploys — it does NOT
# build images. Before the first `terraform apply` that references image_tag, push the 3 custom
# images to the 3 ECR repos this config creates:
#
#   ../../../scripts/build-images.sh -r <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -t <image_tag> -p
#
# (bootstrap order: `terraform apply -target=aws_ecr_repository.fhirbridge_app -target=aws_ecr_repository.demo_app -target=aws_ecr_repository.worker`
# first, then run the build script, then a full `terraform apply`.)
#
# No NAT gateway / private subnets in this first cut (matches the project's own documented "TLS is
# a deliberate later step" posture) — Fargate tasks run in public subnets with public IPs so they
# can pull from ECR without a NAT gateway. SQL Server Express and Redis are pinned to a single task
# each (EFS-backed data directories are not safe for concurrent multi-instance processes) and are
# reachable by the other services via AWS Cloud Map private DNS, never through the ALB.

terraform {
  required_version = ">= 1.6.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  azs = slice(data.aws_availability_zones.available.names, 0, 2)
}

# --- Networking: a small dedicated VPC, 2 public subnets ---

resource "aws_vpc" "main" {
  cidr_block           = var.vpc_cidr
  enable_dns_support   = true
  enable_dns_hostnames = true

  tags = { Name = "${var.name_prefix}-vpc" }
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id

  tags = { Name = "${var.name_prefix}-igw" }
}

resource "aws_subnet" "public" {
  count                   = 2
  vpc_id                  = aws_vpc.main.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 8, count.index)
  availability_zone       = local.azs[count.index]
  map_public_ip_on_launch = true

  tags = { Name = "${var.name_prefix}-public-${count.index}" }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = { Name = "${var.name_prefix}-public-rt" }
}

resource "aws_route_table_association" "public" {
  count          = 2
  subnet_id      = aws_subnet.public[count.index].id
  route_table_id = aws_route_table.public.id
}

# --- Security groups ---

resource "aws_security_group" "alb" {
  name        = "${var.name_prefix}-alb-sg"
  description = "Public ALB — allows inbound HTTP on the app ports."
  vpc_id      = aws_vpc.main.id

  ingress {
    description = "fhirbridge-app"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "demo-app"
    from_port   = 5500
    to_port     = 5500
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "ecs_tasks" {
  name        = "${var.name_prefix}-ecs-tasks-sg"
  description = "All 5 ECS services — ALB reaches the 2 public apps here; sqlserver/redis are only reached by other tasks in this group."
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "From ALB"
    from_port       = 0
    to_port         = 65535
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  ingress {
    description = "Inter-service (sqlserver/redis reachability via Cloud Map)"
    from_port   = 0
    to_port     = 65535
    protocol    = "tcp"
    self        = true
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# --- ECR (custom images are pushed here by the build script) ---

resource "aws_ecr_repository" "fhirbridge_app" {
  name                 = "${var.name_prefix}/fhirbridge-app"
  image_tag_mutability = "MUTABLE"
}

resource "aws_ecr_repository" "demo_app" {
  name                 = "${var.name_prefix}/demo-app"
  image_tag_mutability = "MUTABLE"
}

resource "aws_ecr_repository" "worker" {
  name                 = "${var.name_prefix}/fhirbridge-worker"
  image_tag_mutability = "MUTABLE"
}

# --- ECS cluster ---

resource "aws_ecs_cluster" "main" {
  name = "${var.name_prefix}-cluster"
}

# --- Secrets ---

resource "aws_secretsmanager_secret" "sql_sa_password" {
  name                    = "${var.name_prefix}/sql-sa-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "sql_sa_password" {
  secret_id     = aws_secretsmanager_secret.sql_sa_password.id
  secret_string = var.sql_sa_password
}

resource "aws_secretsmanager_secret" "jwt_signing_key" {
  name                    = "${var.name_prefix}/jwt-signing-key"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "jwt_signing_key" {
  secret_id     = aws_secretsmanager_secret.jwt_signing_key.id
  secret_string = var.jwt_signing_key
}

# --- IAM: task execution role (pulls from ECR, writes logs, reads the 2 secrets above) ---

data "aws_iam_policy_document" "ecs_task_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "ecs_task_execution" {
  name               = "${var.name_prefix}-ecs-task-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_task_assume.json
}

resource "aws_iam_role_policy_attachment" "ecs_task_execution_managed" {
  role       = aws_iam_role.ecs_task_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "ecs_task_execution_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = [
      aws_secretsmanager_secret.sql_sa_password.arn,
      aws_secretsmanager_secret.jwt_signing_key.arn,
    ]
  }
}

resource "aws_iam_role_policy" "ecs_task_execution_secrets" {
  name   = "${var.name_prefix}-ecs-task-execution-secrets"
  role   = aws_iam_role.ecs_task_execution.id
  policy = data.aws_iam_policy_document.ecs_task_execution_secrets.json
}

# --- Log groups (one per service) ---

resource "aws_cloudwatch_log_group" "fhirbridge_app" {
  name              = "/ecs/${var.name_prefix}/fhirbridge-app"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "demo_app" {
  name              = "/ecs/${var.name_prefix}/demo-app"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "worker" {
  name              = "/ecs/${var.name_prefix}/worker"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "sqlserver" {
  name              = "/ecs/${var.name_prefix}/sqlserver"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "redis" {
  name              = "/ecs/${var.name_prefix}/redis"
  retention_in_days = 14
}
