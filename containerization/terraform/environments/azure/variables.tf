variable "name_prefix" {
  description = "Short name used to build resource names (ACR, storage account, Key Vault, Container Apps)."
  type        = string
  default     = "segue"
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

variable "use_azure_postgresql" {
  description = "Chooses which Postgres Segue's own database (FHIRBridgeDb) gets. false (default) keeps a containerized Postgres (azurerm_container_app.postgres — stock postgres:16-alpine, Azure Files-backed persistence, single replica, internal-only network access, requires postgres_password). true creates a managed Azure Database for PostgreSQL Flexible Server instead (see azure_postgresql_sku/azure_postgresql_storage_mb) and points ConnectionStrings:FHIRBridgeDb at it over a required SSL connection — no container, no volume; Azure manages patching/backups/HA. Replaces the SQL Server Express container this deployment used before the app migrated from SQL Server to PostgreSQL — there is no SQL Server option anymore."
  type        = bool
  default     = false
}

variable "postgres_password" {
  description = "Password for Segue's own Postgres database. In the containerized path (use_azure_postgresql = false) this is the 'segue' role's password, stored in Key Vault; in the managed path (true) this is the Flexible Server's administrator_password directly (Azure Database for PostgreSQL doesn't have a separate Key Vault seeding step — the server resource holds it). Required either way — Terraform variables without a default must be provided."
  type        = string
  sensitive   = true
}

variable "postgres_port" {
  description = "Port the containerized Postgres listens on (internal-only). Passed to the container via a postgres -p override. Meaningless when use_azure_postgresql is true — Azure Database for PostgreSQL always uses 5432."
  type        = number
  default     = 5432
}

variable "postgres_external_access" {
  description = "TESTING ONLY: exposes the containerized Postgres directly to the internet (Container Apps external TCP ingress on var.postgres_port) so it can be reached from a local client like psql/pgAdmin. Defaults to false — this deployment is otherwise built around network isolation. Only set to true for a temporary connectivity check, then set back to false and re-apply. Meaningless when use_azure_postgresql is true (Azure Database for PostgreSQL has its own firewall-rule-based access control, see azurerm_postgresql_flexible_server_firewall_rule in main.tf)."
  type        = bool
  default     = false
}

variable "azure_postgresql_sku" {
  description = "Azure Database for PostgreSQL Flexible Server compute/pricing tier, e.g. B_Standard_B1ms (Burstable, cheapest — good for dev/test) or GP_Standard_D2s_v3 (General Purpose, production-appropriate). Only consulted when use_azure_postgresql is true."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "azure_postgresql_storage_mb" {
  description = "Azure Database for PostgreSQL Flexible Server storage size in MB. 32768 (32GB) is the platform minimum. Only consulted when use_azure_postgresql is true — storage can only be scaled up later, not down, so don't over-provision speculatively."
  type        = number
  default     = 32768
}

variable "jwt_signing_key" {
  description = "FHIRBridge.Api's Authentication:SigningKey (HS256). At least 32 random characters."
  type        = string
  sensitive   = true
}

# --- Client-configurable internal ports ---
# segue-app is NOT included here: Azure Container Apps external HTTP ingress
# has no client-configurable port — it's always reached via its https://<app>.<domain> address on
# the platform's standard 443, with no port number in the URL, regardless of target_port. That's a
# genuine Container Apps platform constraint, not a Terraform limitation.
#
# postgres_port/redis_port ARE meaningful to change: they're internal-only TCP ingress (reached by
# the other Container Apps in this environment, never externally — see the postgres/redis
# azurerm_container_app resources below), and main.tf passes each one through to the actual
# process (a `postgres -p` command override for Postgres, a `redis-server --port` command override
# for Redis), not just the ingress target_port. (postgres_port itself is declared further up,
# alongside the other use_azure_postgresql-related variables.)

variable "redis_port" {
  description = "Port Redis listens on (internal-only). Passed to the container via a redis-server --port override."
  type        = number
  default     = 6379
}

variable "use_azure_cache_for_redis" {
  description = "Chooses which Redis this deployment gets. false (default) keeps the existing containerized Redis (azurerm_container_app.redis) — self-signed TLS cert baked into the segue-redis image, Azure Files-backed persistence, single replica, requires redis_password/redis_trusted_certificate_thumbprint. true creates a managed azurerm_redis_cache instead (see azure_cache_sku/azure_cache_capacity) and points ConnectionStrings:Redis at it — no container, no volume, no self-signed cert to generate; Azure issues its own CA-trusted certificate, which FHIRBridge.Api/.Worker accept automatically. redis_password and redis_trusted_certificate_thumbprint are both ignored in this mode — Azure Cache manages its own access keys and presents its own trusted certificate."
  type        = bool
  default     = false
}

variable "azure_cache_sku" {
  description = "Azure Cache for Redis pricing tier — Basic (no SLA, single node, cheapest), Standard (SLA, primary/replica pair), or Premium (SLA, plus VNet injection/clustering/persistence — also switches family to P). Only consulted when use_azure_cache_for_redis is true."
  type        = string
  default     = "Basic"
}

variable "azure_cache_capacity" {
  description = "Azure Cache for Redis size within the chosen sku: 0-6 for Basic/Standard (family C — 0 is the smallest, ~250MB), 1-5 for Premium (family P — 1 is the smallest, 6GB). Only consulted when use_azure_cache_for_redis is true. Defaults to the smallest size for whichever tier is chosen."
  type        = number
  default     = 0
}

variable "redis_password" {
  description = "Password the containerized Redis requires (--requirepass) — defense-in-depth on top of network isolation. Note: since it's passed as a plain container command argument (Redis has no env-var equivalent), it's visible to anyone with read access to this Container App's configuration in the Azure Portal/CLI, same exposure level as any other container command argument. Ignored entirely when use_azure_cache_for_redis is true (Azure Cache manages its own access keys) — still required to be set either way since Terraform variables without a default must be provided, but its value is simply unused in that mode."
  type        = string
  sensitive   = true
  default     = ""
}

variable "redis_trusted_certificate_thumbprint" {
  description = "SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the segue-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn't match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs. REQUIRED (the connection will fail at runtime without it) when use_azure_cache_for_redis is false; ignored entirely — leave blank — when it's true, since Azure Cache presents a normal CA-trusted certificate that needs no pinning."
  type        = string
  default     = ""
}

variable "segue_app_custom_domain" {
  description = "Custom domain for the Segue app (e.g. app.customer.com). Leave blank for *.azurecontainerapps.io. REQUIRED to be blank on the very first apply that creates this app: Azure only assigns customDomainVerificationId once the Container App already exists, and there is no way to know that ID in advance — setting a domain before the app exists always fails with InvalidCustomHostNameValidation (\"a TXT record ... was not found\"), regardless of provider version. Correct order: (1) apply with this blank so the app gets created; (2) read segue_app_domain_verification, create DNS CNAME (domain -> the app's default hostname) + TXT asuid.<domain> = that id, wait for DNS; (3) NOW set this variable and re-apply — registers the hostname (certificate_binding_type Disabled). NOTE: managed-certificate SSL binding (bind_custom_domain_certificates) is currently a no-op — see that variable's description — so the domain resolves to this app but browsers won't get a trusted cert on it yet; front it with your own reverse proxy/CDN cert until the provider migration below happens, or bring your own cert manually."
  type        = string
  default     = ""
}

variable "bind_custom_domain_certificates" {
  description = "Currently a NO-OP: azurerm_container_app_environment_managed_certificate (the resource this flag would create) was only added in terraform-provider-azurerm v4.69.0, and this config is pinned to azurerm ~> 3.100 (see required_providers in main.tf) — no 3.x release has it. The custom-domain resources always use certificate_binding_type = \"Disabled\" regardless of this flag's value. Kept as a variable so existing tfvars files referencing it don't break, and so it's easy to re-wire once this environment is deliberately migrated to azurerm ~> 4.69 (a separate, larger change — see the comment above the azurerm_container_app_custom_domain resources in main.tf)."
  type        = bool
  default     = false
}

variable "enable_tenant_secrets_key_vault" {
  description = "The deployment-time choice between the two secret-storage modes documented in Documents/KeyVault-Implementation.html: false (default) keeps everything — tenant SourceConnection/DestinationConfiguration secrets AND the 4 app-level secrets (jwt-signing-key etc.) — on the local DataProtection-encrypted ProvisionedSecrets DB table, no Azure Key Vault resource created, no extra permission needed. true creates a dedicated Azure Key Vault (RBAC-enabled) via azurerm_key_vault.tenant_secrets, grants the segue_app/worker Container Apps' system-assigned managed identities (and the identity running this apply) the 'Key Vault Secrets Officer' role on it, and points KeyVault:VaultName/KeyVault:UseAzureKeyVault at it — the app then reads/writes secrets there automatically via CompositeSecretProvider/Writer, with the local DB table remaining as an automatic fallback (KeyVault:AllowConfigurationFallback). Granting the RBAC role needs Owner or User Access Administrator on the resource group — a Contributor-only account can still create the vault itself, but will hit an authorization error on the 3 azurerm_role_assignment resources specifically; if that happens, comment them out, apply everything else, then have someone with sufficient rights run the 'az role assignment create' command from main.tf's comment above those resources, using the segue_app_principal_id/worker_principal_id outputs plus your own account's object ID."
  type        = bool
  default     = false
}

variable "enable_seq" {
  description = "false (default) — no Seq container; segue_app/worker log to console/Log Analytics only. true creates a Seq container (datalust/seq, public image) with its own external ingress — its own https://<name_prefix>-seq.<environment>.azurecontainerapps.io URL, protected by var.seq_admin_password — and points Observability:SeqServerUrl at it on segue_app (both the Api and Gateway processes read this same config key) and worker. Unlike Postgres/Redis, Seq gets a public URL deliberately, since the whole point is being able to browse to it and monitor logs."
  type        = bool
  default     = false
}

variable "seq_admin_password" {
  description = "Admin password for the Seq web UI (SEQ_FIRSTRUN_ADMINPASSWORD) — required when enable_seq is true. This is the ONLY thing protecting that URL, since no other authentication is configured here. Ignored when enable_seq is false."
  type        = string
  sensitive   = true
  default     = ""
}

