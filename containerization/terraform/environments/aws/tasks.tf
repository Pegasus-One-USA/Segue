# 4 Fargate task definitions (when using the containerized Postgres path — 3 when
# use_rds_postgresql routes FHIRBridgeDb to Amazon RDS instead). cpu/memory pairs are valid
# Fargate combinations; adjust per load.

# Only created for the containerized Postgres path — Amazon RDS for PostgreSQL (aws_db_instance.
# postgresql in main.tf) needs no ECS task/service of its own. Gated the same as every other
# postgres-specific resource (its Cloud Map entry, EFS access point, Secrets Manager secret, log
# group, and the aws_ecs_service.postgres in services.tf).
resource "aws_ecs_task_definition" "postgres" {
  count                    = var.use_rds_postgresql ? 0 : 1
  family                   = "${var.name_prefix}-postgres"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "1024"
  memory                   = "4096"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  volume {
    name = "postgres-data"
    efs_volume_configuration {
      file_system_id     = aws_efs_file_system.main.id
      transit_encryption = "ENABLED"
      authorization_config {
        access_point_id = aws_efs_access_point.postgres_data[0].id
        iam             = "DISABLED"
      }
    }
  }

  container_definitions = jsonencode([
    {
      name  = "postgres"
      image = "postgres:16-alpine"
      # Stock image, no custom Dockerfile/ECR build needed — unlike Redis (see the redis task
      # below), the C# app enforces no TLS/cert-pinning on the database connection, so this mirrors
      # the SQL Server container it replaces: VPC/security-group isolation (ecs_tasks security
      # group + private subnets) is the real boundary here, not TLS.
      portMappings = [{ containerPort = var.postgres_port, protocol = "tcp" }]
      environment = [
        { name = "POSTGRES_DB", value = "FHIRBridge" },
        { name = "POSTGRES_USER", value = "fhirbridge" },
        # EFS (like Azure Files) doesn't support the chown/chmod postgres's entrypoint does on
        # PGDATA at first boot ("Operation not permitted") the way a native/NFS filesystem does —
        # pointing PGDATA at a subdirectory postgres creates and owns itself (rather than the
        # mount root, which is externally provisioned by the EFS access point above) works around
        # it. Proven previously in this same environment for the (now-removed) HAPI terminology
        # Postgres container.
        { name = "PGDATA", value = "/var/lib/postgresql/data/pgdata" },
      ]
      # Fargate's awsvpc networking has no host-level port remapping — postgres itself must be
      # told to listen on var.postgres_port, not just the portMappings entry above. The image has
      # no env-var port override, so this is passed as a command override instead.
      command = ["postgres", "-p", tostring(var.postgres_port)]
      secrets = [
        { name = "POSTGRES_PASSWORD", valueFrom = aws_secretsmanager_secret.postgres_password[0].arn },
      ]
      mountPoints = [
        { sourceVolume = "postgres-data", containerPath = "/var/lib/postgresql/data", readOnly = false },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.postgres[0].name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "postgres"
        }
      }
    }
  ])
}

resource "aws_ecs_task_definition" "redis" {
  family                   = "${var.name_prefix}-redis"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  volume {
    name = "redis-data"
    efs_volume_configuration {
      file_system_id     = aws_efs_file_system.main.id
      transit_encryption = "ENABLED"
      authorization_config {
        access_point_id = aws_efs_access_point.redis_data.id
        iam             = "DISABLED"
      }
    }
  }

  container_definitions = jsonencode([
    {
      name = "redis"
      # Custom image (not stock redis:7-alpine): FHIRBridge.Api/.Worker refuse a plaintext Redis
      # connection outside Development (HIPAA #15), and stock Redis has no TLS configured at all.
      # See containerization/docker/redis-tls/Dockerfile.
      image        = "${aws_ecr_repository.redis.repository_url}:${var.image_tag}"
      portMappings = [{ containerPort = var.redis_port, protocol = "tcp" }]
      # Redis has no env-var port/password setting — same awsvpc constraint as the containerized
      # Postgres task above for the port. The password is deliberately resolved from $REDIS_PASSWORD inside a shell
      # wrapper rather than passed as a plain --requirepass argument, so the actual value is never
      # written into this task definition in plaintext (unlike the port, which isn't secret) —
      # only the Secrets Manager ARN reference below is. Trade-off: this bypasses the image's
      # entrypoint privilege-drop-to-non-root step, so redis-server runs as root inside its own
      # isolated Fargate task; acceptable here since Fargate isolates at the task/microVM level
      # regardless of in-container UID, and it keeps the secret out of the task definition.
      # --port 0 disables the plaintext port entirely — --tls-port is the only one Redis listens
      # on. --tls-auth-clients no means server-side TLS + the existing --requirepass password,
      # not mutual TLS (no client certificate required) — matches the ConnectionStrings__Redis
      # "ssl=true" (no client cert options) on fhirbridge_app/worker below.
      command = ["sh", "-c", "redis-server --tls-port ${var.redis_port} --port 0 --tls-cert-file /certs/redis.crt --tls-key-file /certs/redis.key --tls-auth-clients no --requirepass \"$REDIS_PASSWORD\""]
      secrets = [
        { name = "REDIS_PASSWORD", valueFrom = aws_secretsmanager_secret.redis_password.arn },
      ]
      mountPoints = [
        { sourceVolume = "redis-data", containerPath = "/data", readOnly = false },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.redis.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "redis"
        }
      }
    }
  ])
}

resource "aws_ecs_task_definition" "fhirbridge_app" {
  family                   = "${var.name_prefix}-app"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "512"
  memory                   = "1024"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  container_definitions = jsonencode([
    {
      name         = "fhirbridge-app"
      image        = "${aws_ecr_repository.fhirbridge_app.repository_url}:${var.image_tag}"
      portMappings = [{ containerPort = 80, protocol = "tcp" }]
      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "ConnectionStrings__FHIRBridgeDb", value = local.fhirbridgedb_connection_string },
        { name = "Database__Provider", value = "PostgreSql" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:${var.redis_port},password=${var.redis_password},ssl=true" },
        { name = "Redis__TrustedCertificateThumbprint", value = var.redis_trusted_certificate_thumbprint },
        { name = "DataProtection__KeyRingPath", value = "/app/keys" },
        { name = "AllowedHosts", value = "*" },
        # Api and Gateway are sibling processes in one container (entrypoint.sh) - Api binds
        # loopback-only on 5000, and Gateway throws at startup outside Development without this.
        { name = "ApiBaseUrl", value = "http://127.0.0.1:5000/" },
      ]
      secrets = [
        { name = "Authentication__SigningKey", valueFrom = aws_secretsmanager_secret.jwt_signing_key.arn },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.fhirbridge_app.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "app"
        }
      }
    }
  ])
}

resource "aws_ecs_task_definition" "worker" {
  family                   = "${var.name_prefix}-worker"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  container_definitions = jsonencode([
    {
      name  = "worker"
      image = "${aws_ecr_repository.worker.repository_url}:${var.image_tag}"
      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "ConnectionStrings__FHIRBridgeDb", value = local.fhirbridgedb_connection_string },
        { name = "Database__Provider", value = "PostgreSql" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:${var.redis_port},password=${var.redis_password},ssl=true" },
        { name = "Redis__TrustedCertificateThumbprint", value = var.redis_trusted_certificate_thumbprint },
        { name = "RuntimeWorker__Enabled", value = "true" },
        { name = "Messaging__Provider", value = "InMemory" },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.worker.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "worker"
        }
      }
    }
  ])
}
