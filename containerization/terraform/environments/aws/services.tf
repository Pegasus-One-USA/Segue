# 5 ECS Fargate services, all running in the private subnets with no public IP (only the ALB is
# internet-facing — see main.tf for the NAT Gateway that lets them still reach ECR/CloudWatch/etc).
# sqlserver/redis are pinned to desired_count = 1 (stateful, EFS-backed — see storage.tf) and
# registered in Cloud Map instead of the ALB. fhirbridge-app/demo-app sit behind the ALB; worker has
# neither.

resource "aws_ecs_service" "sqlserver" {
  name            = "${var.name_prefix}-sqlserver"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.sqlserver.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.sqlserver.arn
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

resource "aws_ecs_service" "hapi_terminology_postgres" {
  name            = "${var.name_prefix}-hapi-terminology-postgres"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.hapi_terminology_postgres.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.hapi_terminology_postgres.arn
  }
}

resource "aws_ecs_service" "hapi_terminology" {
  name            = "${var.name_prefix}-hapi-terminology"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.hapi_terminology.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  # aws_lb_listener.hapi_terminology is itself count-gated by the same variable (0 or 1 instances)
  # — referencing the resource as a whole (not [0]) here keeps this a static list, which
  # depends_on requires, while still depending on it only when it actually exists.
  depends_on = [aws_ecs_service.hapi_terminology_postgres, aws_lb_listener.hapi_terminology]

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  service_registries {
    registry_arn = aws_service_discovery_service.hapi_terminology.arn
  }

  # Only registered with the ALB when var.hapi_terminology_external_access is true — internal
  # reachability (fhirbridge-app/worker via Cloud Map) never depends on this.
  dynamic "load_balancer" {
    for_each = var.hapi_terminology_external_access ? [1] : []
    content {
      target_group_arn = aws_lb_target_group.hapi_terminology[0].arn
      container_name   = "hapi-terminology"
      container_port   = 8080
    }
  }
}

resource "aws_ecs_service" "fhirbridge_app" {
  name            = "${var.name_prefix}-app"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.fhirbridge_app.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  # Connection strings reference sqlserver/redis/hapi-terminology by their predictable Cloud Map
  # hostname rather than a resource attribute, so this dependency has to be spelled out explicitly.
  depends_on = [aws_ecs_service.sqlserver, aws_ecs_service.redis, aws_ecs_service.hapi_terminology, aws_lb_listener.fhirbridge_app]

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

resource "aws_ecs_service" "demo_app" {
  name            = "${var.name_prefix}-demo-app"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.demo_app.arn
  desired_count   = 1
  launch_type     = "FARGATE"

  depends_on = [aws_ecs_service.sqlserver, aws_lb_listener.demo_app]

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.demo_app.arn
    container_name   = "demo-app"
    container_port   = 5500
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
  depends_on = [aws_ecs_service.sqlserver, aws_ecs_service.redis, aws_ecs_service.hapi_terminology, aws_ecs_service.fhirbridge_app]

  network_configuration {
    subnets          = aws_subnet.private[*].id
    security_groups  = [aws_security_group.ecs_tasks.id]
    assign_public_ip = false
  }
}
