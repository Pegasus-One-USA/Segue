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

variable "hapi_terminology_postgres_password" {
  description = "Password for the hapi_terminology Postgres role backing the HAPI terminology server's own schema (internal-only — not the app's own FHIRBridgeDb). Stored in Key Vault like the other secrets above."
  type        = string
  sensitive   = true
}

variable "hapi_terminology_external_access" {
  description = "Exposes the HAPI terminology server externally (Container Apps external ingress) so it can be reached directly from outside this environment — e.g. a separate terminology admin tool, or a third-party integration — rather than only internally by fhirbridge-app/worker. Defaults to false (internal-only, like sqlserver/redis). Setting hapi_terminology_custom_domain also forces this on, since Azure Container Apps custom domains require external ingress."
  type        = bool
  default     = false
}

variable "hapi_terminology_custom_domain" {
  description = "Custom domain for the HAPI terminology server (e.g. terminology.customer.com). Leave blank to use the auto-generated *.azurecontainerapps.io URL once hapi_terminology_external_access is true. Same flow as fhirbridge_app_custom_domain (including the required first-apply-blank step and the current SSL-binding limitation) — see that variable's description and hapi_terminology_domain_verification."
  type        = string
  default     = ""
}

variable "fhirbridge_app_custom_domain" {
  description = "Custom domain for the FHIRBridge app (e.g. app.customer.com). Leave blank for *.azurecontainerapps.io. REQUIRED to be blank on the very first apply that creates this app: Azure only assigns customDomainVerificationId once the Container App already exists, and there is no way to know that ID in advance — setting a domain before the app exists always fails with InvalidCustomHostNameValidation (\"a TXT record ... was not found\"), regardless of provider version. Correct order: (1) apply with this blank so the app gets created; (2) read fhirbridge_app_domain_verification, create DNS CNAME (domain -> the app's default hostname) + TXT asuid.<domain> = that id, wait for DNS; (3) NOW set this variable and re-apply — registers the hostname (certificate_binding_type Disabled). NOTE: managed-certificate SSL binding (bind_custom_domain_certificates) is currently a no-op — see that variable's description — so the domain resolves to this app but browsers won't get a trusted cert on it yet; front it with your own reverse proxy/CDN cert until the provider migration below happens, or bring your own cert manually."
  type        = string
  default     = ""
}

variable "demo_app_custom_domain" {
  description = "Custom domain for the Demo app. Same flow as fhirbridge_app_custom_domain (including the required first-apply-blank step and the current SSL-binding limitation)."
  type        = string
  default     = ""
}

variable "bind_custom_domain_certificates" {
  description = "Currently a NO-OP: azurerm_container_app_environment_managed_certificate (the resource this flag would create) was only added in terraform-provider-azurerm v4.69.0, and this config is pinned to azurerm ~> 3.100 (see required_providers in main.tf) — no 3.x release has it. The custom-domain resources always use certificate_binding_type = \"Disabled\" regardless of this flag's value. Kept as a variable so existing tfvars files referencing it don't break, and so it's easy to re-wire once this environment is deliberately migrated to azurerm ~> 4.69 (a separate, larger change — see the comment above the azurerm_container_app_custom_domain resources in main.tf)."
  type        = bool
  default     = false
}

variable "sql_external_access" {
  description = "TESTING ONLY: exposes SQL Server directly to the internet (Container Apps external TCP ingress on var.sql_port) so it can be reached from a local client like SSMS. Defaults to false — this deployment is otherwise built around network isolation (sqlserver/redis are internal-only by design), and this bypasses that deliberately. Only set to true for a temporary connectivity check, then set back to false and re-apply. Even with this on, the sa password (from Key Vault) is still required to connect — this only controls network reachability, not authentication."
  type        = bool
  default     = false
}
