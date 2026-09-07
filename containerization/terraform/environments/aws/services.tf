# 4 ECS Fargate services when using the containerized Postgres path (3 when use_rds_postgresql
# routes FHIRBridgeDb to Amazon RDS instead), all running in the private subnets with no public IP
# (only the ALB is internet-facing — see main.tf for the NAT Gateway that lets them still reach
# ECR/CloudWatch/etc). postgres/redis are pinned to desired_count = 1 (stateful, EFS-backed — see
# storage.tf) and registered in Cloud Map instead of the ALB. fhirbridge-app sits behind the ALB;
# worker has neither.

# Only created for the containerized Postgres path — see aws_ecs_task_definition.postgres's
# comment in tasks.tf for the full list of postgres-specific resources this is gated the same as.
resource "aws_ecs_service" "postgres" {
  count           = var.use_rds_postgresql ? 0 : 1
  name            = "${var.name_prefix}-postgres"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.postgres[0].arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.postgres[0].arn
  }
}

resource "aws_ecs_service" "redis" {
  name            = "${var.name_prefix}-redis"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.redis.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.redis.arn
  }
}

resource "aws_ecs_service" "fhirbridge_app" {
  name            = "${var.name_prefix}-app"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.fhirbridge_app.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  # Connection strings reference postgres (containerized path)/redis by their predictable Cloud
  # Map hostname rather than a resource attribute, so this dependency has to be spelled out
  # explicitly for those two. The Amazon RDS path doesn't need an equivalent explicit dependency —
  # local.fhirbridgedb_connection_string references aws_db_instance.postgresql directly, so
  # Terraform infers that dependency automatically. Referencing aws_ecs_service.postgres here is
  # safe even when use_rds_postgresql is true (count = 0): it just resolves to zero dependencies.
  depends_on = [aws_ecs_service.postgres, aws_ecs_service.redis, aws_lb_listener.fhirbridge_app]

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.fhirbridge_app.arn
    container_name   = "fhirbridge-app"
    container_port   = 80
  }
}

resource "aws_ecs_service" "worker" {
  name            = "${var.name_prefix}-worker"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.worker.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  # fhirbridge_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. ECS restarts a failed
  # task automatically (desired_count reconciliation), which turns a lost race into a self-healing
  # retry.
  depends_on = [aws_ecs_service.postgres, aws_ecs_service.redis, aws_ecs_service.fhirbridge_app]

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }
}
