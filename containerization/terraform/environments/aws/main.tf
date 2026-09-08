# Deploys the same 4-container topology to AWS ECS Fargate. Terraform only deploys — it does NOT
# build images. Before the first `terraform apply` that references image_tag, push the 3 custom
# images to the 3 ECR repos this config creates:
#
#   ../../../scripts/build-images.sh -r <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -t <image_tag> -p
#
# (bootstrap order: `terraform apply -target=aws_ecr_repository.fhirbridge_app -target=aws_ecr_repository.worker -target=aws_ecr_repository.redis`
# first, then run the build script, then a full `terraform apply`.)
#
# All 4 containers run in PRIVATE subnets with no public IP — only the ALB is internet-facing.
# A single NAT Gateway (one AZ, not HA — a documented cost/simplicity trade-off) lets the private
# subnets still reach ECR/CloudWatch/Secrets Manager/the internet for image pulls. Redis is pinned
# to a single task (EFS-backed data directories are not safe for concurrent multi-instance
# processes) and is reachable by the other services via AWS Cloud Map private DNS, never through
# the ALB. FHIRBridge's own database (Postgres) is either that same containerized/single-task/
# internal-only pattern, or a managed Amazon RDS for PostgreSQL instance — see use_rds_postgresql
# in variables.tf. The ALB
# terminates TLS using a self-signed certificate generated at apply time — swap in a real ACM
# certificate (DNS-validated against a real domain) once one exists; see alb.tf.

terraform {
  required_version = ">= 1.6.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
  }
}

provider "aws" {
  region = var.aws_region

  # Applies to every resource below that supports tags, automatically — no need to repeat a
  # tags = {...} block on each one. Lets you find/filter/cost-report on everything this
  # deployment created, and is what the tag-based teardown path (see
  # ../../../scripts/cleanup-aws.sh|ps1 and the containerization guide) matches against.
  default_tags {
    tags = {
      Project     = "FHIRBridge"
      Component   = "containerization"
      Environment = var.name_prefix
      ManagedBy   = "Terraform"
    }
  }
}

data "aws_availability_zones" "available" {
  state = "available"
}

locals {
  azs = slice(data.aws_availability_zones.available.names, 0, 2)

  # Single source of truth for both fhirbridge_app's and worker's ConnectionStrings__FHIRBridgeDb
  # (was duplicated identically in both places before this became a local) — resolves to whichever
  # of the two Postgres resources use_rds_postgresql actually created. Amazon RDS for PostgreSQL
  # requires/strongly recommends SSL and presents an AWS-issued certificate (unlike the
  # containerized path's plain internal-network-only connection, which relies on the ecs_tasks
  # security group + private subnets instead of TLS — matching how the containerized SQL Server
  # container this replaced was itself "network isolation only, no cert pinning needed").
  fhirbridgedb_connection_string = var.use_rds_postgresql ? (
    "Host=${aws_db_instance.postgresql[0].address};Port=5432;Database=${aws_db_instance.postgresql[0].db_name};Username=${aws_db_instance.postgresql[0].username};Password=${var.postgres_password};Ssl Mode=Require;"
    ) : (
    "Host=postgres.${var.name_prefix}.internal;Port=${var.postgres_port};Database=FHIRBridge;Username=fhirbridge;Password=${var.postgres_password};"
  )
}

# --- Networking: a small dedicated VPC, 2 public subnets (ALB only) + 2 private subnets (everything else) ---

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

# Private subnets — where every container actually runs. Only the ALB (in the public subnets
# above) is directly internet-routable.
resource "aws_subnet" "private" {
  count                   = 2
  vpc_id                  = aws_vpc.main.id
  cidr_block              = cidrsubnet(var.vpc_cidr, 8, count.index + 10)
  availability_zone       = local.azs[count.index]
  map_public_ip_on_launch = false

  tags = { Name = "${var.name_prefix}-private-${count.index}" }
}

# A single NAT Gateway (one AZ, not multi-AZ/HA) so the private subnets can still reach ECR,
# CloudWatch, Secrets Manager, etc. — a deliberate cost/simplicity trade-off for this scale; a
# production-scale deployment would typically add one NAT Gateway per AZ instead.
resource "aws_eip" "nat" {
  domain = "vpc"
  tags   = { Name = "${var.name_prefix}-nat-eip" }
}

resource "aws_nat_gateway" "main" {
  allocation_id = aws_eip.nat.id
  subnet_id     = aws_subnet.public[0].id
  depends_on    = [aws_internet_gateway.main]

  tags = { Name = "${var.name_prefix}-nat" }
}

resource "aws_route_table" "private" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block     = "0.0.0.0/0"
    nat_gateway_id = aws_nat_gateway.main.id
  }

  tags = { Name = "${var.name_prefix}-private-rt" }
}

resource "aws_route_table_association" "private" {
  count          = 2
  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private.id
}

# --- Security groups ---

resource "aws_security_group" "alb" {
  name        = "${var.name_prefix}-alb-sg"
  description = "Public ALB — allows inbound HTTP on the app port (and the Seq port, if enabled)."
  vpc_id      = aws_vpc.main.id

  ingress {
    description = "fhirbridge-app"
    from_port   = var.fhirbridge_app_port
    to_port     = var.fhirbridge_app_port
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  dynamic "ingress" {
    for_each = var.enable_seq ? [1] : []
    content {
      description = "seq"
      from_port   = var.seq_port
      to_port     = var.seq_port
      protocol    = "tcp"
      cidr_blocks = ["0.0.0.0/0"]
    }
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
  description = "All 4 ECS services — ALB reaches the public app (fhirbridge-app) here; postgres/redis are only reached by other tasks in this group."
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "From ALB"
    from_port       = 0
    to_port         = 65535
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }

  ingress {
    description = "Inter-service (postgres/redis reachability via Cloud Map)"
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

resource "aws_ecr_repository" "worker" {
  name                 = "${var.name_prefix}/fhirbridge-worker"
  image_tag_mutability = "MUTABLE"
}

# Stock redis:7-alpine plus a fixed, committed self-signed TLS certificate — see
# containerization/docker/redis-tls/Dockerfile's own comment for why Redis needs a custom image at
# all (FHIRBridge.Api/.Worker refuse a plaintext Redis connection outside Development).
resource "aws_ecr_repository" "redis" {
  name                 = "${var.name_prefix}/fhirbridge-redis"
  image_tag_mutability = "MUTABLE"
}

# --- ECS cluster ---

resource "aws_ecs_cluster" "main" {
  name = "${var.name_prefix}-cluster"
}

# --- Secrets ---

# Only needed for the containerized Postgres path — Amazon RDS for PostgreSQL takes
# postgres_password directly as its master password (aws_db_instance.postgresql below), with no
# separate Secrets Manager seeding step of its own. Gated the same as the postgres ECS task
# definition/service.
resource "aws_secretsmanager_secret" "postgres_password" {
  count                   = var.use_rds_postgresql ? 0 : 1
  name                    = "${var.name_prefix}/postgres-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "postgres_password" {
  count         = var.use_rds_postgresql ? 0 : 1
  secret_id     = aws_secretsmanager_secret.postgres_password[0].id
  secret_string = var.postgres_password
}

resource "aws_secretsmanager_secret" "jwt_signing_key" {
  name                    = "${var.name_prefix}/jwt-signing-key"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "jwt_signing_key" {
  secret_id     = aws_secretsmanager_secret.jwt_signing_key.id
  secret_string = var.jwt_signing_key
}

resource "aws_secretsmanager_secret" "redis_password" {
  name                    = "${var.name_prefix}/redis-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "redis_password" {
  secret_id     = aws_secretsmanager_secret.redis_password.id
  secret_string = var.redis_password
}

resource "aws_secretsmanager_secret" "seq_admin_password" {
  count                   = var.enable_seq ? 1 : 0
  name                    = "${var.name_prefix}/seq-admin-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "seq_admin_password" {
  count         = var.enable_seq ? 1 : 0
  secret_id     = aws_secretsmanager_secret.seq_admin_password[0].id
  secret_string = var.seq_admin_password
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
    resources = concat(
      [
        aws_secretsmanager_secret.jwt_signing_key.arn,
        aws_secretsmanager_secret.redis_password.arn,
      ],
      # postgres_password only exists in Secrets Manager for the containerized path — the RDS
      # path's master password is set directly on aws_db_instance.postgresql, not via this secret.
      var.use_rds_postgresql ? [] : [aws_secretsmanager_secret.postgres_password[0].arn],
      var.enable_seq ? [aws_secretsmanager_secret.seq_admin_password[0].arn] : [],
    )
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

resource "aws_cloudwatch_log_group" "worker" {
  name              = "/ecs/${var.name_prefix}/worker"
  retention_in_days = 14
}

# Only needed for the containerized Postgres path — RDS ships its own logs to CloudWatch
# separately (not via an ECS task log driver). Gated the same as the postgres ECS task
# definition/service.
resource "aws_cloudwatch_log_group" "postgres" {
  count             = var.use_rds_postgresql ? 0 : 1
  name              = "/ecs/${var.name_prefix}/postgres"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "redis" {
  name              = "/ecs/${var.name_prefix}/redis"
  retention_in_days = 14
}

resource "aws_cloudwatch_log_group" "seq" {
  count             = var.enable_seq ? 1 : 0
  name              = "/ecs/${var.name_prefix}/seq"
  retention_in_days = 14
}

# --- FHIRBridge's own database: Amazon RDS for PostgreSQL — only when use_rds_postgresql is
# true. A standard aws_db_instance (engine = "postgres"), not Aurora — Aurora is a separate, more
# complex distributed engine and would be overkill for the simple "managed PaaS Postgres" this
# toggle is meant to provide (the direct AWS equivalent of the Azure environment's Azure Database
# for PostgreSQL Flexible Server). Sits in the same private subnets the ECS tasks already use (no
# separate DB subnet topology introduced), with its own security group allowing inbound 5432 from
# the ecs_tasks security group only — never publicly accessible. Replaces the SQL Server Express
# container this deployment used before the app migrated from SQL Server to PostgreSQL. ---

resource "aws_db_subnet_group" "postgresql" {
  count      = var.use_rds_postgresql ? 1 : 0
  name       = "${var.name_prefix}-postgresql"
  subnet_ids = aws_subnet.private[*].id

  tags = { Name = "${var.name_prefix}-postgresql-subnet-group" }
}

resource "aws_security_group" "rds_postgresql" {
  count       = var.use_rds_postgresql ? 1 : 0
  name        = "${var.name_prefix}-rds-postgresql-sg"
  description = "RDS for PostgreSQL — allows inbound 5432 from the ECS tasks security group only."
  vpc_id      = aws_vpc.main.id

  ingress {
    description     = "Postgres from ECS tasks (fhirbridge-app/worker)"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.ecs_tasks.id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_db_instance" "postgresql" {
  count                  = var.use_rds_postgresql ? 1 : 0
  identifier             = "${var.name_prefix}-postgresql"
  engine                 = "postgres"
  engine_version         = "16"
  instance_class         = var.rds_postgresql_instance_class
  allocated_storage      = var.rds_postgresql_allocated_storage
  db_name                = "FHIRBridge"
  username               = "fhirbridge"
  password               = var.postgres_password
  db_subnet_group_name   = aws_db_subnet_group.postgresql[0].name
  vpc_security_group_ids = [aws_security_group.rds_postgresql[0].id]
  publicly_accessible    = false

  # Matches this environment's existing recovery_window_in_days = 0 posture on its Secrets Manager
  # entries — this is a dev/test-scale deployment, not one with a production backup/retention
  # policy, so a final snapshot on destroy would just be one more manually-cleaned-up resource.
  skip_final_snapshot = true

  tags = { Name = "${var.name_prefix}-postgresql" }
}

# --- Self-signed TLS certificate for the ALB ---
# Encrypts traffic immediately with zero external dependencies. Browsers will show a trust warning
# (expected — there's no real domain behind it yet, so no CA will vouch for it). Replace with a
# real, DNS-validated ACM certificate (aws_acm_certificate + aws_acm_certificate_validation) as
# soon as a real domain exists — that's a certificate swap in alb.tf, not an architecture change.

resource "tls_private_key" "alb" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "tls_self_signed_cert" "alb" {
  private_key_pem = tls_private_key.alb.private_key_pem

  subject {
    common_name  = "${var.name_prefix}.local"
    organization = "FHIRBridge"
  }

  validity_period_hours = 8760 # 1 year — regenerate (terraform apply) to renew
  allowed_uses = [
    "key_encipherment",
    "digital_signature",
    "server_auth",
  ]
}

resource "aws_acm_certificate" "alb" {
  private_key      = tls_private_key.alb.private_key_pem
  certificate_body = tls_self_signed_cert.alb.cert_pem
  tags             = { Name = "${var.name_prefix}-self-signed" }

  lifecycle {
    create_before_destroy = true
  }
}
