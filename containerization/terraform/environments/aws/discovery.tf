# AWS Cloud Map private DNS namespace — lets fhirbridge-app/demo-app/worker reach sqlserver and
# redis by hostname (sqlserver.fhirbridge.internal, redis.fhirbridge.internal) the same way they'd
# reach a container-name service in Docker Compose, without going through the ALB.

resource "aws_service_discovery_private_dns_namespace" "main" {
  name = "${var.name_prefix}.internal"
  vpc  = aws_vpc.main.id
}

resource "aws_service_discovery_service" "sqlserver" {
  name = "sqlserver"

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
