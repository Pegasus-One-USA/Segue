variable "name_prefix" {
  description = "Short name used to build resource names (VPC, cluster, ECR repos, ALB, etc.)."
  type        = string
  default     = "fhirbridge"
}

variable "aws_region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "vpc_cidr" {
  description = "CIDR block for the dedicated VPC this stack provisions."
  type        = string
  default     = "10.90.0.0/16"
}

variable "image_tag" {
  description = "Tag the 3 custom images were pushed to ECR with (containerization/scripts/build-images.sh|ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -Tag <this> -Push)."
  type        = string
  default     = "latest"
}

variable "sql_sa_password" {
  description = "SQL Server SA password. Must satisfy SQL Server's complexity policy."
  type        = string
  sensitive   = true
}

variable "jwt_signing_key" {
  description = "FHIRBridge.Api's Authentication:SigningKey (HS256). At least 32 random characters."
  type        = string
  sensitive   = true
}

# --- Client-configurable ports ---
# fhirbridge_app_port/demo_app_port only change the ALB listener (what a client types after the
# ALB's DNS name) — the containers themselves keep listening on their own fixed internal ports
# (80 / 5500, baked into the images), so changing these never requires a rebuild.
#
# sql_port/redis_port are different: AWS Fargate's "awsvpc" networking has no host-level port
# remapping the way Docker Compose does, so the database/cache process itself must actually listen
# on the port declared here — tasks.tf passes each one through to the container (MSSQL_TCP_PORT
# for SQL Server, a `redis-server --port` override for Redis) rather than just relabeling a port
# mapping. Both are internal-only (reached via Cloud Map, never by an external client — see
# discovery.tf), so this only matters if you need them on non-default ports for your own network
# conventions.

variable "fhirbridge_app_port" {
  description = "Public port clients use to reach fhirbridge-app through the load balancer."
  type        = number
  default     = 80
}

variable "demo_app_port" {
  description = "Public port clients use to reach demo-app through the load balancer."
  type        = number
  default     = 5500
}

variable "sql_port" {
  description = "Port SQL Server Express listens on (internal-only, via Cloud Map). Passed to the container as MSSQL_TCP_PORT."
  type        = number
  default     = 1433
}

variable "redis_port" {
  description = "Port Redis listens on (internal-only, via Cloud Map). Passed to the container via a redis-server --port override."
  type        = number
  default     = 6379
}

variable "redis_password" {
  description = "Password Redis requires (--requirepass) — defense-in-depth on top of network isolation. Stored in Secrets Manager and injected as an env var at runtime, never written into the task definition in plaintext (see the redis task's command override in tasks.tf)."
  type        = string
  sensitive   = true
}

variable "redis_trusted_certificate_thumbprint" {
  description = "SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the fhirbridge-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn't match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs."
  type        = string
}

variable "hapi_terminology_postgres_password" {
  description = "Password for the hapi_terminology Postgres role backing the HAPI terminology server's own schema (internal-only, via Cloud Map — not the app's FHIRBridgeDb). Stored in Secrets Manager."
  type        = string
  sensitive   = true
}

variable "hapi_terminology_external_access" {
  description = "Exposes the HAPI terminology server externally through the ALB (https://<alb-dns-name-or-your-own-domain>:hapi_terminology_port), for cases where it needs to be reached directly from outside the VPC (e.g. a separate terminology admin tool, or a third-party integration) rather than only internally by fhirbridge-app/worker. Defaults to false — internal-only via Cloud Map, matching sqlserver/redis. There is no per-service domain binding on this shared ALB the way Azure Container Apps has per-app custom domains (see the azure environment for that) — point your own DNS (CNAME) at the ALB's DNS name instead (aws_lb.main.dns_name, or the hapi_terminology_url output below), and swap aws_acm_certificate.alb for a real, DNS-validated certificate for your domain instead of the self-signed one (see main.tf)."
  type        = bool
  default     = false
}

variable "hapi_terminology_port" {
  description = "Public port clients use to reach the HAPI terminology server through the load balancer, when hapi_terminology_external_access is true."
  type        = number
  default     = 8090
}
