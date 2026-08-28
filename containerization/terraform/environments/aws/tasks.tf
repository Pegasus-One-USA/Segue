# 5 Fargate task definitions. cpu/memory pairs are valid Fargate combinations; adjust per load.

resource "aws_ecs_task_definition" "sqlserver" {
  family                   = "${var.name_prefix}-sqlserver"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "1024"
  memory                   = "4096"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  volume {
    name = "sql-data"
    efs_volume_configuration {
      file_system_id     = aws_efs_file_system.main.id
      transit_encryption = "ENABLED"
      authorization_config {
        access_point_id = aws_efs_access_point.sql_data.id
        iam             = "DISABLED"
      }
    }
  }

  container_definitions = jsonencode([
    {
      name         = "sqlserver"
      image        = "mcr.microsoft.com/mssql/server:2022-latest"
      portMappings = [{ containerPort = var.sql_port, protocol = "tcp" }]
      environment = [
        { name = "ACCEPT_EULA", value = "Y" },
        { name = "MSSQL_PID", value = "Express" },
        # Fargate's awsvpc networking has no host-level port remapping — SQL Server itself must be
        # told to listen on var.sql_port, not just the portMappings entry above.
        { name = "MSSQL_TCP_PORT", value = tostring(var.sql_port) },
      ]
      secrets = [
        { name = "MSSQL_SA_PASSWORD", valueFrom = aws_secretsmanager_secret.sql_sa_password.arn },
      ]
      mountPoints = [
        { sourceVolume = "sql-data", containerPath = "/var/opt/mssql", readOnly = false },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.sqlserver.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "sqlserver"
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
      # Redis has no env-var port/password setting — same awsvpc constraint as SQL Server above
      # for the port. The password is deliberately resolved from $REDIS_PASSWORD inside a shell
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

resource "aws_ecs_task_definition" "hapi_terminology_postgres" {
  family                   = "${var.name_prefix}-hapi-terminology-postgres"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "512"
  memory                   = "1024"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  volume {
    name = "hapi-terminology-postgres-data"
    efs_volume_configuration {
      file_system_id     = aws_efs_file_system.main.id
      transit_encryption = "ENABLED"
      authorization_config {
        access_point_id = aws_efs_access_point.hapi_terminology_postgres_data.id
        iam             = "DISABLED"
      }
    }
  }

  container_definitions = jsonencode([
    {
      name         = "hapi-terminology-postgres"
      image        = "postgres:16-alpine"
      portMappings = [{ containerPort = 5432, protocol = "tcp" }]
      environment = [
        { name = "POSTGRES_DB", value = "hapi_terminology" },
        { name = "POSTGRES_USER", value = "hapi_terminology" },
      ]
      secrets = [
        { name = "POSTGRES_PASSWORD", valueFrom = aws_secretsmanager_secret.hapi_terminology_postgres_password.arn },
      ]
      mountPoints = [
        { sourceVolume = "hapi-terminology-postgres-data", containerPath = "/var/lib/postgresql/data", readOnly = false },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.hapi_terminology_postgres.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "hapi-terminology-postgres"
        }
      }
    }
  ])
}

# Second, dedicated HAPI FHIR instance (+ its own Postgres above) used only for code-system
# lookups/validation/expansion/translation ($lookup et al.) and the automatic vocabulary syncs in
# FHIRBridge.Infrastructure/Terminology/Hapi — separate from any EHR-sourced FHIR data, which never
# touches this service. Reached only via Cloud Map (hapi-terminology.<name_prefix>.internal), never
# through the ALB — see Terminology__BaseUrl on fhirbridge_app/worker below.
resource "aws_ecs_task_definition" "hapi_terminology" {
  family                   = "${var.name_prefix}-hapi-terminology"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "512"
  memory                   = "2048"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  container_definitions = jsonencode([
    {
      name         = "hapi-terminology"
      image        = "hapiproject/hapi:latest"
      portMappings = [{ containerPort = 8080, protocol = "tcp" }]
      environment = [
        { name = "SPRING_DATASOURCE_URL", value = "jdbc:postgresql://hapi-terminology-postgres.${var.name_prefix}.internal:5432/hapi_terminology" },
        { name = "SPRING_DATASOURCE_USERNAME", value = "hapi_terminology" },
        { name = "SPRING_DATASOURCE_DRIVERCLASSNAME", value = "org.postgresql.Driver" },
        { name = "SPRING_JPA_PROPERTIES_HIBERNATE_DIALECT", value = "ca.uhn.fhir.jpa.model.dialect.HapiFhirPostgres94Dialect" },
        { name = "HAPI_FHIR_VERSION", value = "R4" },
      ]
      secrets = [
        { name = "SPRING_DATASOURCE_PASSWORD", valueFrom = aws_secretsmanager_secret.hapi_terminology_postgres_password.arn },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.hapi_terminology.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "hapi-terminology"
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
        { name = "ConnectionStrings__FHIRBridgeDb", value = "Server=sqlserver.${var.name_prefix}.internal,${var.sql_port};Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:${var.redis_port},password=${var.redis_password},ssl=true" },
        { name = "DataProtection__KeyRingPath", value = "/app/keys" },
        # The demo app is a separate origin whose frontend calls this API cross-origin — the ALB's
        # DNS name is known from this same apply (a different resource, not a self-reference).
        { name = "Portal__AllowedOrigins__0", value = "https://${aws_lb.main.dns_name}:${var.demo_app_port}" },
        { name = "AllowedHosts", value = "*" },
        { name = "Terminology__BaseUrl", value = "http://hapi-terminology.${var.name_prefix}.internal:8080/fhir" },
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

resource "aws_ecs_task_definition" "demo_app" {
  family                   = "${var.name_prefix}-demo-app"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.ecs_task_execution.arn

  container_definitions = jsonencode([
    {
      name         = "demo-app"
      image        = "${aws_ecr_repository.demo_app.repository_url}:${var.image_tag}"
      portMappings = [{ containerPort = 5500, protocol = "tcp" }]
      environment = [
        { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
        { name = "ConnectionStrings__Default", value = "Server=sqlserver.${var.name_prefix}.internal,${var.sql_port};Database=HealthAppDb;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True" },
        { name = "AllowedFrontendOrigin", value = "https://${aws_lb.main.dns_name}:${var.demo_app_port}" },
      ]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          "awslogs-group"         = aws_cloudwatch_log_group.demo_app.name
          "awslogs-region"        = var.aws_region
          "awslogs-stream-prefix" = "demo-app"
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
        { name = "ConnectionStrings__FHIRBridgeDb", value = "Server=sqlserver.${var.name_prefix}.internal,${var.sql_port};Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:${var.redis_port},password=${var.redis_password},ssl=true" },
        { name = "RuntimeWorker__Enabled", value = "true" },
        { name = "Messaging__Provider", value = "InMemory" },
        { name = "Terminology__BaseUrl", value = "http://hapi-terminology.${var.name_prefix}.internal:8080/fhir" },
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
