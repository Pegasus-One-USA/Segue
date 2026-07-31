variable "name_prefix" {
  description = "Short name used to build resource names (ACR, storage account, Key Vault, Container Apps)."
  type        = string
  default     = "fhirbridge"
}

variable "resource_group_name" {
  description = "Name of the EXISTING resource group every resource in this deployment is created into. This config never creates or destroys the resource group itself (see the data \"azurerm_resource_group\" \"main\" block in main.tf) — create it once yourself first if it doesn't already exist: az group create --name <this> --location <region>. The region for every resource here is inherited from this group, not set separately."
  type        = string
  default     = "rg-tusharpuri"
}

variable "image_tag" {
  description = "Tag the 3 custom images were pushed to ACR with (containerization/scripts/build-images.sh|ps1 -Registry <this ACR's login server> -Tag <this> -Push)."
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

# --- Client-configurable internal ports ---
# fhirbridge-app and demo-app are NOT included here: Azure Container Apps external HTTP ingress
# has no client-configurable port — it's always reached via its https://<app>.<domain> address on
# the platform's standard 443, with no port number in the URL, regardless of target_port. That's a
# genuine Container Apps platform constraint, not a Terraform limitation.
#
# sql_port/redis_port ARE meaningful to change: they're internal-only TCP ingress (reached by the
# other Container Apps in this environment, never externally — see the sqlserver/redis
# azurerm_container_app resources below), and main.tf passes each one through to the actual
# process (MSSQL_TCP_PORT for SQL Server, a `redis-server --port` command override for Redis), not
# just the ingress target_port.

variable "sql_port" {
  description = "Port SQL Server Express listens on (internal-only). Passed to the container as MSSQL_TCP_PORT."
  type        = number
  default     = 1433
}

variable "redis_port" {
  description = "Port Redis listens on (internal-only). Passed to the container via a redis-server --port override."
  type        = number
  default     = 6379
}

variable "redis_password" {
  description = "Password Redis requires (--requirepass) — defense-in-depth on top of network isolation. Note: since it's passed as a plain container command argument (Redis has no env-var equivalent), it's visible to anyone with read access to this Container App's configuration in the Azure Portal/CLI, same exposure level as any other container command argument."
  type        = string
  sensitive   = true
}

variable "fhirbridge_app_custom_domain" {
  description = "Custom domain for the FHIRBridge app (e.g. app.customer.com). Leave blank (default) to keep using the auto-generated *.azurecontainerapps.io URL. Setting this requires a two-phase apply: (1) apply with this left blank, read the fhirbridge_app_domain_verification output, add a CNAME (pointing this domain at fhirbridge_app_url's hostname) and a TXT record named asuid.<this domain> (value = that output) at your DNS provider, wait for DNS to propagate; (2) set this variable to the domain and re-apply — this provisions a free Azure-managed certificate (which validates the TXT record at apply time, so it fails if DNS isn't ready) and binds the domain."
  type        = string
  default     = ""
}

variable "demo_app_custom_domain" {
  description = "Custom domain for the Demo app. Same two-phase flow as fhirbridge_app_custom_domain — see the demo_app_domain_verification output."
  type        = string
  default     = ""
}

variable "sql_external_access" {
  description = "TESTING ONLY: exposes SQL Server directly to the internet (Container Apps external TCP ingress on var.sql_port) so it can be reached from a local client like SSMS. Defaults to false — this deployment is otherwise built around network isolation (sqlserver/redis are internal-only by design), and this bypasses that deliberately. Only set to true for a temporary connectivity check, then set back to false and re-apply. Even with this on, the sa password (from Key Vault) is still required to connect — this only controls network reachability, not authentication."
  type        = bool
  default     = false
}
