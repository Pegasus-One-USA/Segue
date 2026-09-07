variable "image_tag" {
  description = "Tag the 3 custom images were built with (containerization/scripts/build-images.sh|ps1 -Tag ...)."
  type        = string
  default     = "local"
}

variable "postgres_password" {
  description = "Password for the 'fhirbridge' role in FHIRBridge's own containerized Postgres database. Injected as POSTGRES_PASSWORD on the postgres container and used to build both fhirbridge-app's and worker's ConnectionStrings__FHIRBridgeDb. Replaces the former sql_sa_password variable now that this environment has moved off SQL Server."
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

variable "postgres_host_port" {
  description = "Host port for the containerized Postgres backing FHIRBridgeDb. Offset from Postgres's standard 5432 to avoid colliding with any native/other local Postgres install on the host machine (docker-compose.yml itself doesn't run a Postgres for FHIRBridgeDb, so there's no compose-stack mapping to avoid here the way redis_host_port is offset from compose's own redis mapping)."
  type        = number
  default     = 5433
}

variable "redis_host_port" {
  description = "Host port for the Redis container. Offset from 6379 to avoid colliding with the repo-root dev/E2E docker-compose.yml stack."
  type        = number
  default     = 6380
}

