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
