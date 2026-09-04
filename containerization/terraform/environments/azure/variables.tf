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

variable "redis_trusted_certificate_thumbprint" {
  description = "SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the fhirbridge-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn't match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs."
  type        = string
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

variable "enable_tenant_secrets_key_vault" {
  description = "The deployment-time choice between the two secret-storage modes documented in Documents/KeyVault-Implementation.html: false (default) keeps everything — tenant SourceConnection/DestinationConfiguration secrets AND the 4 app-level secrets (jwt-signing-key etc.) — on the local DataProtection-encrypted ProvisionedSecrets DB table, no Azure Key Vault resource created, no extra permission needed. true creates a dedicated Azure Key Vault (RBAC-enabled) via azurerm_key_vault.tenant_secrets, grants the fhirbridge_app/worker Container Apps' system-assigned managed identities (and the identity running this apply) the 'Key Vault Secrets Officer' role on it, and points KeyVault:VaultName/KeyVault:UseAzureKeyVault at it — the app then reads/writes secrets there automatically via CompositeSecretProvider/Writer, with the local DB table remaining as an automatic fallback (KeyVault:AllowConfigurationFallback). Granting the RBAC role needs Owner or User Access Administrator on the resource group — a Contributor-only account can still create the vault itself, but will hit an authorization error on the 3 azurerm_role_assignment resources specifically; if that happens, comment them out, apply everything else, then have someone with sufficient rights run the 'az role assignment create' command from main.tf's comment above those resources, using the fhirbridge_app_principal_id/worker_principal_id outputs plus your own account's object ID."
  type        = bool
  default     = false
}

variable "sql_external_access" {
  description = "TESTING ONLY: exposes SQL Server directly to the internet (Container Apps external TCP ingress on var.sql_port) so it can be reached from a local client like SSMS. Defaults to false — this deployment is otherwise built around network isolation (sqlserver/redis are internal-only by design), and this bypasses that deliberately. Only set to true for a temporary connectivity check, then set back to false and re-apply. Even with this on, the sa password (from Key Vault) is still required to connect — this only controls network reachability, not authentication."
  type        = bool
  default     = false
}
