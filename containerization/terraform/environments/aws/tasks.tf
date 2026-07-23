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
      name  = "sqlserver"
      image = "mcr.microsoft.com/mssql/server:2022-latest"
      portMappings = [{ containerPort = 1433, protocol = "tcp" }]
      environment = [
        { name = "ACCEPT_EULA", value = "Y" },
        { name = "MSSQL_PID", value = "Express" },
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
      name         = "redis"
      image        = "redis:7-alpine"
      portMappings = [{ containerPort = 6379, protocol = "tcp" }]
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
        { name = "ConnectionStrings__FHIRBridgeDb", value = "Server=sqlserver.${var.name_prefix}.internal,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:6379" },
        { name = "DataProtection__KeyRingPath", value = "/app/keys" },
        # The demo app is a separate origin whose frontend calls this API cross-origin — the ALB's
        # DNS name is known from this same apply (a different resource, not a self-reference).
        { name = "Portal__AllowedOrigins__0", value = "http://${aws_lb.main.dns_name}:5500" },
        { name = "AllowedHosts", value = "*" },
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
        { name = "ConnectionStrings__Default", value = "Server=sqlserver.${var.name_prefix}.internal,1433;Database=HealthAppDb;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True" },
        { name = "AllowedFrontendOrigin", value = "http://${aws_lb.main.dns_name}:5500" },
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
        { name = "ConnectionStrings__FHIRBridgeDb", value = "Server=sqlserver.${var.name_prefix}.internal,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True" },
        { name = "ConnectionStrings__Redis", value = "redis.${var.name_prefix}.internal:6379" },
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
