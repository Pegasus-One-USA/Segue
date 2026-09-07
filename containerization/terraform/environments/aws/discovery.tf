# AWS Cloud Map private DNS namespace — lets fhirbridge-app/worker reach postgres (when
# use_rds_postgresql is false) and redis by hostname (postgres.fhirbridge.internal,
# redis.fhirbridge.internal, etc.) the same way they'd reach a container-name service in Docker
# Compose, without going through the ALB.

resource "aws_service_discovery_private_dns_namespace" "main" {
  name = "${var.name_prefix}.internal"
  vpc  = aws_vpc.main.id
}

# Only needed for the containerized Postgres path — Amazon RDS for PostgreSQL is reached by its
# own DNS endpoint (aws_db_instance.postgresql[0].address), not Cloud Map. Gated the same as the
# postgres ECS task definition/service.
resource "aws_service_discovery_service" "postgres" {
  count = var.use_rds_postgresql ? 0 : 1
  name  = "postgres"

  dns_config {
    namespace_id = aws_service_discovery_private_dns_namespace.main.id
    dns_records {
      ttl  = 10
      type = "A"
    }
    routing_policy = "MULTIVALUE"
  }
}

resource "aws_service_discovery_service" "redis" {
  name = "redis"

  dns_config {
    namespace_id = aws_service_discovery_private_dns_namespace.main.id
    dns_records {
      ttl  = 10
      type = "A"
    }
    routing_policy = "MULTIVALUE"
  }
}
