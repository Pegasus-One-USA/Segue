variable "image_tag" {
  description = "Tag the 3 custom images were built with (containerization/scripts/build-images.sh|ps1 -Tag ...)."
  type        = string
  default     = "local"
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

variable "redis_password" {
  description = "Password Redis requires (--requirepass) — defense-in-depth on top of network isolation."
  type        = string
  sensitive   = true
}

variable "redis_trusted_certificate_thumbprint" {
  description = "SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the fhirbridge-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn't match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs."
  type        = string
}

variable "hapi_terminology_postgres_password" {
  description = "Password for the hapi_terminology Postgres role backing the HAPI terminology server's own schema (not the app's own FHIRBridgeDb)."
  type        = string
  sensitive   = true
}

variable "portal_build_config" {
  description = "Angular build configuration baked into the fhirbridge-app image (informational only here — the image is already built by the time Terraform runs)."
  type        = string
  default     = "production"
}

variable "app_host_port" {
  description = "Host port for fhirbridge-app (Gateway, serves the portal + proxies /api)."
  type        = number
  default     = 8080
}

variable "demo_host_port" {
  description = "Host port for demo-app."
  type        = number
  default     = 5500
}

variable "sql_host_port" {
  description = "Host port for the SQL Server Express container. Offset from 1433 to avoid colliding with the repo-root dev/E2E docker-compose.yml stack."
  type        = number
  default     = 1434
}

variable "redis_host_port" {
  description = "Host port for the Redis container. Offset from 6379 to avoid colliding with the repo-root dev/E2E docker-compose.yml stack."
  type        = number
  default     = 6380
}

variable "hapi_terminology_host_port" {
  description = "Host port for the HAPI terminology server. Offset from 8090 to avoid colliding with the repo-root dev/E2E docker-compose.yml stack's own hapi-terminology mapping."
  type        = number
  default     = 8091
}
