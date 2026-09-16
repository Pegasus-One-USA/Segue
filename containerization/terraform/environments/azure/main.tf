# Deploys the same 4-container topology to Azure Container Apps. Terraform only deploys — it does
# NOT build images. Before `terraform apply`, run:
#
#   ../../../scripts/build-images.sh -r <acr-login-server> -t <image_tag> -p
#
# against the ACR this config creates (its login server is in the `acr_login_server` output —
# create the ACR first with a targeted apply, or push after the first apply and re-apply to update
# the Container Apps' image references).
#
# Segue's own database (Postgres) and Redis are each either the same
# containerized/single-replica/internal-only pattern (Azure Files-backed data directories are not
# safe for concurrent multi-instance processes; reachable by other Container Apps in the same
# environment via its app name as hostname, Container Apps' built-in internal DNS — never exposed
# externally) or a managed Azure PaaS service — see use_azure_postgresql/use_azure_cache_for_redis
# in variables.tf.

terraform {
  required_version = ">= 1.6.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    # By default, destroying a Key Vault also tries to immediately purge it (a separate, more
    # privileged action than deleting it — Microsoft.KeyVault/locations/deletedVaults/purge/action,
    # which needs a role most Contributor-level accounts don't have). Disabling this means
    # `terraform destroy` only soft-deletes the vault; it then sits in the 7-day soft-delete
    # retention window (soft_delete_retention_days on the resource) before Azure purges it
    # automatically — harmless, and avoids requiring that extra permission just to tear down.
    key_vault {
      purge_soft_delete_on_destroy = false
    }

    # Log Analytics workspaces have the opposite default problem from Key Vault: deleting one
    # normally just soft-deletes it (kept recoverable for a retention window), so it lingers in
    # `az resource list`/the Portal even after `terraform destroy` reports success. Unlike Key
    # Vault's purge, permanently deleting a workspace doesn't need any extra permission beyond the
    # normal delete action this account already has — so opt into it, keeping cleanup actually
    # clean instead of leaving a soft-deleted workspace behind every time.
    log_analytics_workspace {
      permanently_delete_on_destroy = true
    }
  }

  # Without this, the provider tries to auto-register every resource provider it supports
  # (Kusto, AVS, Databricks, etc.) on first use — a subscription-level action many accounts don't
  # have, especially ones scoped only to a single resource group. This deployment only needs a
  # handful of common providers (ContainerRegistry, App, Storage, KeyVault, OperationalInsights)
  # that are already registered on virtually any subscription that's been used before, so skip
  # auto-registration entirely rather than requiring subscription-owner-level permissions just to
  # `terraform apply`. If a genuinely unregistered provider is needed later, register it once with
  # `az provider register --namespace Microsoft.<X>` and re-apply.
  skip_provider_registration = true
}

data "azurerm_client_config" "current" {}

resource "random_id" "suffix" {
  byte_length = 3
}

# Key Vault gets its OWN independent random suffix, deliberately not sharing random_id.suffix with
# the ACR/storage account names above. Key Vault names are globally reserved even while
# soft-deleted (unlike other resource types here), so a previous deployment's vault name can stay
# blocked for its full soft-delete retention window if it can never be purged or recovered (e.g. an
# account without Microsoft.KeyVault/locations/deletedVaults/{read,purge} rights — see the
# containerization run log). Giving the vault its own randomness means a stuck old vault name never
# blocks a fresh deployment, even if random_id.suffix ends up reused (e.g. an interrupted destroy).
resource "random_id" "kv_suffix" {
  byte_length = 3
}

# Own random component too, same reasoning as kv_suffix above, kept independent from it so the two
# vaults' names never collide and a stuck/soft-deleted one never blocks the other. Generated
# unconditionally (harmless if enable_tenant_secrets_key_vault ends up false — nothing consumes it).
resource "random_id" "tenant_kv_suffix" {
  byte_length = 3
}

locals {
  suffix               = random_id.suffix.hex
  acr_name             = "${var.name_prefix}acr${local.suffix}"             # ACR: alnum only, globally unique
  storage_account_name = "${var.name_prefix}st${local.suffix}"              # Storage account: alnum only, <=24 chars, globally unique
  key_vault_name       = "${var.name_prefix}-kv-${random_id.kv_suffix.hex}" # Key Vault: alnum + hyphens, <=24 chars, globally unique, own random component
  # Deliberately shortened to "tkv" (not "tenant-kv") to stay within Key Vault's 24-char name cap
  # once name_prefix is longer (e.g. "segue-tkv-a1b2c3" = 21 chars).
  tenant_secrets_key_vault_name = "${var.name_prefix}-tkv-${random_id.tenant_kv_suffix.hex}"

  # Plain-string app names (not resource attribute lookups) so a Container App can safely compute
  # its OWN public URL — Container Apps' FQDN is always "<app-name>.<environment-default-domain>",
  # and the environment's default_domain doesn't depend on any individual app, so this avoids the
  # self-reference a resource would otherwise need to read its own computed attributes.
  postgres_name       = "${var.name_prefix}-postgres"
  redis_name          = "${var.name_prefix}-redis"
  segue_app_name = "${var.name_prefix}-app"
  worker_name         = "${var.name_prefix}-worker"
  seq_name            = "${var.name_prefix}-seq"

  # Single source of truth for both segue_app's and worker's ConnectionStrings__Redis (was
  # duplicated identically in both places before this became a local) — resolves to whichever of
  # the two Redis resources use_azure_cache_for_redis actually created. Azure Cache's own
  # abortConnect=false matches StackExchange.Redis's usual recommended default for a managed
  # service (retries instead of failing fast on a transient connect issue); the containerized path
  # doesn't set it, matching its pre-existing behavior.
  redis_connection_string = var.use_azure_cache_for_redis ? (
    "${azurerm_redis_cache.main[0].hostname}:${azurerm_redis_cache.main[0].ssl_port},password=${azurerm_redis_cache.main[0].primary_access_key},ssl=true,abortConnect=false"
    ) : (
    "${local.redis_name}:${var.redis_port},password=${azurerm_key_vault_secret.redis_password[0].value},ssl=true"
  )

  # Single source of truth for both segue_app's and worker's ConnectionStrings__FHIRBridgeDb.
  # Azure Database for PostgreSQL requires SSL by default and presents a real CA-trusted
  # certificate (unlike the containerized path's plain internal-network-only connection, which
  # relies on Container Apps' network isolation instead of TLS — matching how the containerized
  # postgres/redis paths are already "internal only, no external exposure" by design.
  seguedb_connection_string = var.use_azure_postgresql ? (
    "Host=${azurerm_postgresql_flexible_server.main[0].fqdn};Port=5432;Database=${azurerm_postgresql_flexible_server_database.main[0].name};Username=${azurerm_postgresql_flexible_server.main[0].administrator_login};Password=${var.postgres_password};Ssl Mode=Require;"
    ) : (
    "Host=${local.postgres_name};Port=${var.postgres_port};Database=Segue;Username=segue;Password=${azurerm_key_vault_secret.postgres_password[0].value};"
  )

  # Applied to every resource below that supports `tags` — lets you find/filter/cost-report on
  # everything this deployment created, and is what the tag-based teardown path (see
  # ../../../azure-deploy/cleanup.sh|ps1's -UseTags mode, and az cli one-liners in the
  # containerization guide) matches against instead of relying on Terraform state alone.
  common_tags = {
    Project     = "Segue"
    Component   = "containerization"
    Environment = var.name_prefix
    ManagedBy   = "Terraform"
  }
}

# Every resource in this deployment lands in this ONE pre-existing resource group — nothing here
# creates or destroys the group itself, and `terraform destroy` will remove everything Terraform
# created inside it while leaving the group in place. The group must already exist before
# `terraform apply` (create it once with `az group create --name <var.resource_group_name>
# --location <region>` if it doesn't).
data "azurerm_resource_group" "main" {
  name = var.resource_group_name
}

# --- Container registry (custom images are pushed here by the build script) ---

resource "azurerm_container_registry" "acr" {
  name                = local.acr_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = data.azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = true
  tags                = local.common_tags
}

# --- Container Apps environment ---

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${var.name_prefix}-logs"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = data.azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.common_tags
}

resource "azurerm_container_app_environment" "main" {
  name                       = "${var.name_prefix}-env"
  resource_group_name        = data.azurerm_resource_group.main.name
  location                   = data.azurerm_resource_group.main.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  tags                       = local.common_tags
}

# --- Persistent storage for the containerized Postgres + Redis paths (Container Apps are otherwise stateless) ---

resource "azurerm_storage_account" "main" {
  name                     = local.storage_account_name
  resource_group_name      = data.azurerm_resource_group.main.name
  location                 = data.azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  tags                     = local.common_tags
}

# Only needed for the containerized Postgres path — Azure Database for PostgreSQL is a managed
# PaaS service with no Azure Files volume of its own. Gated the same as azurerm_container_app.postgres below.
resource "azurerm_storage_share" "postgres_data" {
  count                = var.use_azure_postgresql ? 0 : 1
  name                 = "postgres-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 50
}

# Only needed for the containerized Redis path — Azure Cache for Redis is a managed PaaS service
# with no Azure Files volume of its own. Gated the same as azurerm_container_app.redis below.
resource "azurerm_storage_share" "redis_data" {
  count                = var.use_azure_cache_for_redis ? 0 : 1
  name                 = "redis-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 10
}

resource "azurerm_container_app_environment_storage" "postgres_data" {
  count                        = var.use_azure_postgresql ? 0 : 1
  name                         = "postgres-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.postgres_data[0].name
  access_mode                  = "ReadWrite"
}

resource "azurerm_container_app_environment_storage" "redis_data" {
  count                        = var.use_azure_cache_for_redis ? 0 : 1
  name                         = "redis-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.redis_data[0].name
  access_mode                  = "ReadWrite"
}

resource "azurerm_storage_share" "keys_data" {
  name                 = "keys-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 1
}

resource "azurerm_container_app_environment_storage" "keys_data" {
  name                         = "keys-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.keys_data.name
  access_mode                  = "ReadWrite"
}

# Only needed when var.enable_seq is true. Untested against the same Azure Files/SMB permission
# limitation documented on the Postgres container above (see that resource's comments and
# Documents/Containerization-Azure-Deployment-Run-Log-V2.html) — Seq may or may not hit the same
# wall; if its container fails at startup with a similar permission error, the same fix pattern
# (a custom local-disk-plus-backup image) would need to be applied here too.
resource "azurerm_storage_share" "seq_data" {
  count                = var.enable_seq ? 1 : 0
  name                 = "seq-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 10
}

resource "azurerm_container_app_environment_storage" "seq_data" {
  count                        = var.enable_seq ? 1 : 0
  name                         = "seq-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.seq_data[0].name
  access_mode                  = "ReadWrite"
}

# --- Secrets (Key Vault is the source of truth; Terraform variables only seed it) ---
#
# The postgres_password/jwt_signing_key/redis_password Terraform variables still exist as the
# initial seed values, but every Container App below reads the *Key Vault secret's* .value, not
# the variable directly. `lifecycle.ignore_changes = ["value"]` means that after the first apply,
# rotating a secret in the Vault (Portal, CLI, or an external rotation process) sticks — a later
# `terraform apply` with the old tfvars value won't overwrite it.
#
# Classic vault Access Policies (not RBAC authorization) are used deliberately: granting the
# applying identity access via RBAC requires `Microsoft.Authorization/roleAssignments/write` on
# the vault, which needs Owner or User Access Administrator — many real-world accounts only have
# Contributor (which can create/manage the vault itself, just not hand out RBAC roles on it).
# Access Policies are set via the vault resource directly (`Microsoft.KeyVault/vaults/write`),
# which Contributor already includes, so this works without needing an elevated role grant first.

resource "azurerm_key_vault" "main" {
  name                       = local.key_vault_name
  resource_group_name        = data.azurerm_resource_group.main.name
  location                   = data.azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  enable_rbac_authorization  = false
  soft_delete_retention_days = 7
  tags                       = local.common_tags
}

# Grants the identity running `terraform apply` permission to seed secret values. Real day-2
# rotation is expected to happen via whatever identity/process the client wires up separately
# (Portal, CLI, CI/CD) — this policy only unblocks the initial `terraform apply`.
resource "azurerm_key_vault_access_policy" "terraform_kv_secrets" {
  key_vault_id = azurerm_key_vault.main.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = data.azurerm_client_config.current.object_id

  secret_permissions = ["Get", "List", "Set", "Delete", "Purge"]
}

# Only needed for the containerized Postgres path — Azure Database for PostgreSQL manages its own
# admin password directly on the server resource, not via this Key Vault.
resource "azurerm_key_vault_secret" "postgres_password" {
  count        = var.use_azure_postgresql ? 0 : 1
  name         = "postgres-password"
  value        = var.postgres_password
  key_vault_id = azurerm_key_vault.main.id
  tags         = local.common_tags
  depends_on   = [azurerm_key_vault_access_policy.terraform_kv_secrets]

  lifecycle {
    ignore_changes = [value]
  }
}

resource "azurerm_key_vault_secret" "jwt_signing_key" {
  name         = "jwt-signing-key"
  value        = var.jwt_signing_key
  key_vault_id = azurerm_key_vault.main.id
  tags         = local.common_tags
  depends_on   = [azurerm_key_vault_access_policy.terraform_kv_secrets]

  lifecycle {
    ignore_changes = [value]
  }
}

# Only needed for the containerized Redis path — Azure Cache for Redis generates and manages its
# own access keys, var.redis_password is simply unused when use_azure_cache_for_redis is true.
resource "azurerm_key_vault_secret" "redis_password" {
  count        = var.use_azure_cache_for_redis ? 0 : 1
  name         = "redis-password"
  value        = var.redis_password
  key_vault_id = azurerm_key_vault.main.id
  tags         = local.common_tags
  depends_on   = [azurerm_key_vault_access_policy.terraform_kv_secrets]

  lifecycle {
    ignore_changes = [value]
  }
}

# --- Tenant secrets Key Vault (SourceConnection/DestinationConfiguration + app-level secrets) ---
#
# Separate from azurerm_key_vault.main above (which only ever holds 3 infra bootstrap secrets seeded
# once by Terraform itself and then left alone — lifecycle.ignore_changes = [value]) — this vault is
# what the running app reads/writes to continuously at runtime via CompositeSecretProvider/Writer
# (tenant SourceConnection/DestinationConfiguration secrets, plus the 4 app-level secrets —
# jwt-signing-key etc. — AppSecretProvisioner self-provisions on first boot). Created only when
# var.enable_tenant_secrets_key_vault is true; false (the default) leaves everything on the local
# DataProtection-encrypted ProvisionedSecrets DB table with no Key Vault, no extra Azure resource,
# no extra permission to grant. RBAC-enabled (not classic Access Policies, unlike the vault above) —
# azurerm_role_assignment below, not azurerm_key_vault_access_policy.
resource "azurerm_key_vault" "tenant_secrets" {
  count                      = var.enable_tenant_secrets_key_vault ? 1 : 0
  name                       = local.tenant_secrets_key_vault_name
  resource_group_name        = data.azurerm_resource_group.main.name
  location                   = data.azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  enable_rbac_authorization  = true
  soft_delete_retention_days = 7
  tags                       = local.common_tags
}

# Assigning a role needs Microsoft.Authorization/roleAssignments/write (Owner or User Access
# Administrator) on the vault/resource group — a Contributor-only account can create the vault above
# just fine but will get an authorization error on these three specifically. If that happens:
# comment them out, apply everything else, then have someone with sufficient rights run, for each
# principal_id below:
#   az role assignment create --role "Key Vault Secrets Officer" --assignee <principal_id> \
#     --scope <tenant_secrets_key_vault_id output>
# The applying identity gets the same role as the app/worker (not just the two Container Apps) so
# whoever ran `terraform apply` can also read/set secrets directly afterward — e.g. to seed the
# initial values a real Epic/destination connection needs, or debug what's actually stored.
resource "azurerm_role_assignment" "tenant_secrets_terraform_applier" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "tenant_secrets_segue_app" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_container_app.segue_app.identity[0].principal_id
}

resource "azurerm_role_assignment" "tenant_secrets_worker" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_container_app.worker.identity[0].principal_id
}

# The 4 app-level secrets AppSecretProvisioner otherwise self-provisions on first boot (see
# AppSecretReferences.cs) — pre-seeding them here means the app never needs that first-boot
# generate-and-write round trip to succeed at all. Random values match
# AppSecretValueGenerator.Generate()'s own shape exactly (32 random bytes, base64-encoded) via
# random_id's b64_std output. Unprefixed names on purpose: this vault is dedicated to this one
# deployment (not shared across environments), so KeyVault:SecretPrefix is deliberately left unset
# above — see Documents/KeyVault-Implementation.html's Mode A/B section for why a shared vault would
# need a prefix and a dedicated one doesn't. ignore_changes on value, same as the infra-bootstrap
# vault's secrets above — once set, only rotate through the app's own admin tooling
# (AppSecretsAdminService) or 'az keyvault secret set', never by re-applying this config.
#
# depends_on the role assignment above (not just the vault) because RBAC data-plane writes need
# that role actually granted first — if this hits a transient 403 on the very first apply (Azure AD
# role-assignment propagation can lag a few seconds behind the assignment's own creation), just
# re-run apply; nothing else needs to change.
resource "random_id" "jwt_signing_key" {
  count       = var.enable_tenant_secrets_key_vault ? 1 : 0
  byte_length = 32
}

resource "azurerm_key_vault_secret" "app_jwt_signing_key" {
  count        = var.enable_tenant_secrets_key_vault ? 1 : 0
  name         = "jwt-signing-key"
  value        = random_id.jwt_signing_key[0].b64_std
  key_vault_id = azurerm_key_vault.tenant_secrets[0].id
  tags         = local.common_tags
  depends_on   = [azurerm_role_assignment.tenant_secrets_terraform_applier]

  lifecycle {
    ignore_changes = [value]
  }
}

resource "random_id" "download_link_signing_secret" {
  count       = var.enable_tenant_secrets_key_vault ? 1 : 0
  byte_length = 32
}

resource "azurerm_key_vault_secret" "app_download_link_signing_secret" {
  count        = var.enable_tenant_secrets_key_vault ? 1 : 0
  name         = "download-link-signing-secret"
  value        = random_id.download_link_signing_secret[0].b64_std
  key_vault_id = azurerm_key_vault.tenant_secrets[0].id
  tags         = local.common_tags
  depends_on   = [azurerm_role_assignment.tenant_secrets_terraform_applier]

  lifecycle {
    ignore_changes = [value]
  }
}

resource "random_id" "transform_hashing_key" {
  count       = var.enable_tenant_secrets_key_vault ? 1 : 0
  byte_length = 32
}

resource "azurerm_key_vault_secret" "app_transform_hashing_key" {
  count        = var.enable_tenant_secrets_key_vault ? 1 : 0
  name         = "transform-hashing-key"
  value        = random_id.transform_hashing_key[0].b64_std
  key_vault_id = azurerm_key_vault.tenant_secrets[0].id
  tags         = local.common_tags
  depends_on   = [azurerm_role_assignment.tenant_secrets_terraform_applier]

  lifecycle {
    ignore_changes = [value]
  }
}

resource "random_id" "phi_encryption_key" {
  count       = var.enable_tenant_secrets_key_vault ? 1 : 0
  byte_length = 32
}

# Never rotate this one — see AppSecretReferences.PhiEncryptionKey's own doc comment: doing so
# leaves every previously-encrypted execution-history row permanently undecryptable. The
# ignore_changes below already protects it from an accidental value change on re-apply.
resource "azurerm_key_vault_secret" "app_phi_encryption_key" {
  count        = var.enable_tenant_secrets_key_vault ? 1 : 0
  name         = "phi-encryption-key"
  value        = random_id.phi_encryption_key[0].b64_std
  key_vault_id = azurerm_key_vault.tenant_secrets[0].id
  tags         = local.common_tags
  depends_on   = [azurerm_role_assignment.tenant_secrets_terraform_applier]

  lifecycle {
    ignore_changes = [value]
  }
}

# DataProtection key-ring protection (Documents/KeyVault-Implementation.html §3/§7's
# DataProtection:KeyVaultKeyId) — wraps the app's DataProtection key ring using this Key's
# wrap/unwrap operations instead of a local certificate. Tied to the same toggle as the secrets
# above rather than a second variable: this deployment's whole point in turning Key Vault on is to
# get everything off local/ephemeral storage, and a Key object is cheap or free to also provision
# once the vault itself already exists.
#
# Creating a Key object (not just a Secret) needs Key Vault Crypto OFFICER — a different, broader
# role than what the app itself needs at runtime (Crypto USER, wrap/unwrap only — granted to
# segue_app/worker below).
resource "azurerm_role_assignment" "tenant_secrets_terraform_applier_crypto" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_key" "dataprotection" {
  count        = var.enable_tenant_secrets_key_vault ? 1 : 0
  name         = "${var.name_prefix}-dataprotection-key"
  key_vault_id = azurerm_key_vault.tenant_secrets[0].id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["wrapKey", "unwrapKey"]
  tags         = local.common_tags

  depends_on = [azurerm_role_assignment.tenant_secrets_terraform_applier_crypto]
}

resource "azurerm_role_assignment" "tenant_secrets_segue_app_crypto" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_container_app.segue_app.identity[0].principal_id
}

resource "azurerm_role_assignment" "tenant_secrets_worker_crypto" {
  count                = var.enable_tenant_secrets_key_vault ? 1 : 0
  scope                = azurerm_key_vault.tenant_secrets[0].id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_container_app.worker.identity[0].principal_id
}

# --- Segue's own database: containerized Postgres (internal only, single replica) — only
# when NOT using Azure Database for PostgreSQL. Replaces the SQL Server Express container this
# deployment used before the app migrated from SQL Server to PostgreSQL. ---

resource "azurerm_container_app" "postgres" {
  count                        = var.use_azure_postgresql ? 0 : 1
  name                         = local.postgres_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  secret {
    name  = "postgres-password"
    value = azurerm_key_vault_secret.postgres_password[0].value
  }

  template {
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "postgres-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.postgres_data[0].name
    }

    container {
      name   = "postgres"
      image  = "postgres:16-alpine"
      cpu    = 1.0
      memory = "2Gi"

      env {
        name  = "POSTGRES_DB"
        value = "Segue"
      }
      env {
        name  = "POSTGRES_USER"
        value = "segue"
      }
      env {
        name        = "POSTGRES_PASSWORD"
        secret_name = "postgres-password"
      }
      # Azure Files (SMB) doesn't support the chown/chmod postgres's entrypoint does on PGDATA at
      # first boot ("Operation not permitted") the way a native/NFS filesystem does — pointing
      # PGDATA at a subdirectory postgres creates and owns itself (rather than the mount root,
      # which is externally provisioned) works around it.
      env {
        name  = "PGDATA"
        value = "/var/lib/postgresql/data/pgdata"
      }

      # Postgres has no env-var port override — this passes -p through to the postgres binary to
      # listen on var.postgres_port, matching the ingress target_port below. Deliberately `args`,
      # NOT `command`: `command` replaces the image's ENTRYPOINT outright, which for
      # postgres:16-alpine is docker-entrypoint.sh — the script that does the chown/chmod on PGDATA
      # referenced above AND drops from root to the unprivileged `postgres` user via gosu before
      # exec'ing the server. A `command` override skips it, running the postgres binary directly as
      # root, which postgres refuses outright ("must not be run as root"), crash-looping the
      # container — confirmed live via the equivalent Bicep template's identical bug (see
      # main.bicep's postgresApp resource). `args` instead overrides only the image's CMD, leaving
      # ENTRYPOINT (docker-entrypoint.sh) intact — entrypoint.sh sees the leading `-p` and
      # auto-prepends `postgres` itself, so the effective command stays `postgres -p <port>`, just
      # routed through the privilege-dropping entrypoint instead of around it.
      args = ["-p", tostring(var.postgres_port)]

      volume_mounts {
        name = "postgres-data"
        path = "/var/lib/postgresql/data"
      }
    }
  }

  ingress {
    # See var.postgres_external_access's description — defaults to false (internal-only, as this
    # deployment is otherwise built around network isolation). Only ever set to true deliberately,
    # for a temporary connectivity check (e.g. connecting with psql/pgAdmin), then revert and re-apply.
    external_enabled = var.postgres_external_access
    target_port      = var.postgres_port
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Segue's own database: Azure Database for PostgreSQL Flexible Server — only when
# use_azure_postgresql is true. No VNet in this deployment (Container Apps here use the platform's
# own managed networking, not a customer VNet), so this uses public network access + a firewall
# rule allowing Azure-internal traffic, rather than private VNet integration — the simplest setup
# that still keeps the server unreachable from the public internet by anything except Azure's own
# services (and, indirectly, this environment's Container Apps). ---

resource "azurerm_postgresql_flexible_server" "main" {
  count                  = var.use_azure_postgresql ? 1 : 0
  name                   = "${var.name_prefix}-pg"
  resource_group_name    = data.azurerm_resource_group.main.name
  location               = data.azurerm_resource_group.main.location
  version                = "16"
  administrator_login    = "segueadmin"
  administrator_password = var.postgres_password
  storage_mb             = var.azure_postgresql_storage_mb
  sku_name               = var.azure_postgresql_sku
  zone                   = null
  tags                   = local.common_tags

  lifecycle {
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "allow_azure_services" {
  count            = var.use_azure_postgresql ? 1 : 0
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.main[0].id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_postgresql_flexible_server_database" "main" {
  count     = var.use_azure_postgresql ? 1 : 0
  name      = "Segue"
  server_id = azurerm_postgresql_flexible_server.main[0].id
  collation = "en_US.utf8"
  charset   = "UTF8"
}

# --- Redis (internal only, single replica) — only when NOT using Azure Cache for Redis ---

resource "azurerm_container_app" "redis" {
  count                        = var.use_azure_cache_for_redis ? 0 : 1
  name                         = local.redis_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  secret {
    name  = "acr-password"
    value = azurerm_container_registry.acr.admin_password
  }

  registry {
    server               = azurerm_container_registry.acr.login_server
    username             = azurerm_container_registry.acr.admin_username
    password_secret_name = "acr-password"
  }

  template {
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "redis-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.redis_data[0].name
    }

    container {
      name = "redis"
      # Custom image (not stock redis:7-alpine): FHIRBridge.Api/.Worker refuse a plaintext Redis
      # connection outside Development (HIPAA #15), and stock Redis has no TLS configured at all.
      # See containerization/docker/redis-tls/Dockerfile.
      image  = "${azurerm_container_registry.acr.login_server}/segue-redis:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"
      # Redis has no env-var port/password override — this command override tells the redis-server
      # process itself to listen on var.redis_port and require var.redis_password, matching the
      # ingress target_port below (requirepass is defense-in-depth on top of network isolation).
      # --port 0 disables the plaintext port entirely — --tls-port is the only one Redis listens
      # on. --tls-auth-clients no means server-side TLS + --requirepass, not mutual TLS (no client
      # certificate required) — matches ConnectionStrings__Redis's "ssl=true" (no client cert
      # options) on segue_app/worker below.
      command = [
        "redis-server",
        "--tls-port", tostring(var.redis_port),
        "--port", "0",
        "--tls-cert-file", "/certs/redis.crt",
        "--tls-key-file", "/certs/redis.key",
        "--tls-auth-clients", "no",
        "--requirepass", azurerm_key_vault_secret.redis_password[0].value,
      ]

      volume_mounts {
        name = "redis-data"
        path = "/data"
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = var.redis_port
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Azure Cache for Redis — only when use_azure_cache_for_redis is true ---
#
# Managed alternative to the containerized Redis above: no container, no Azure Files volume, no
# self-signed TLS cert to generate/bake into an image — Azure issues a normal CA-trusted
# certificate, so FHIRBridge.Api/.Worker's ValidateRedisServerCertificate (see
# DependencyInjection.cs) accepts it automatically as long as Redis:TrustedCertificateThumbprint is
# left UNSET (see the dynamic "env" blocks below — that value is only ever emitted for the
# containerized path). minimum_tls_version 1.2 matches what the app already requires
# (ConnectionStrings:Redis must include ssl=true outside Development).
resource "azurerm_redis_cache" "main" {
  count                = var.use_azure_cache_for_redis ? 1 : 0
  name                 = "${var.name_prefix}-cache"
  resource_group_name  = data.azurerm_resource_group.main.name
  location             = data.azurerm_resource_group.main.location
  capacity             = var.azure_cache_capacity
  family               = var.azure_cache_sku == "Premium" ? "P" : "C"
  sku_name             = var.azure_cache_sku
  minimum_tls_version  = "1.2"
  non_ssl_port_enabled = false
  tags                 = local.common_tags
}

# --- Seq (structured log viewing) — only when var.enable_seq is true. Public image, no custom
#     build needed. External ingress deliberately: unlike Postgres/Redis (internal-only, reached
#     only by segue_app/worker), Seq exists specifically so a human can browse to it and
#     monitor logs — an internal-only Seq would have no way in from outside the Container Apps
#     environment. ---

resource "azurerm_container_app" "seq" {
  count                        = var.enable_seq ? 1 : 0
  name                         = local.seq_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  secret {
    name  = "seq-admin-password"
    value = var.seq_admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "seq-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.seq_data[0].name
    }

    container {
      name   = "seq"
      image  = "datalust/seq:latest"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "ACCEPT_EULA"
        value = "Y"
      }
      env {
        name        = "SEQ_FIRSTRUN_ADMINPASSWORD"
        secret_name = "seq-admin-password"
      }

      volume_mounts {
        name = "seq-data"
        path = "/data"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 80
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Segue app (Api + Gateway), public ---

resource "azurerm_container_app" "segue_app" {
  name                         = local.segue_app_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  # Redis is reached by its plain (predictable) name rather than a resource attribute in the
  # containerized path, so this dependency has to be spelled out explicitly. The Postgres
  # connection (both containerized and managed) is referenced via a resource attribute inside
  # local.seguedb_connection_string instead, so Terraform infers that dependency automatically.
  depends_on = [azurerm_container_app.redis]

  # System-assigned so DefaultAzureCredential (AzureKeyVaultSecretProvider/Writer) can authenticate
  # to the tenant secrets Key Vault with no credential material to manage. Added unconditionally —
  # harmless when enable_tenant_secrets_key_vault is false, and this identity may be reused for other
  # Azure resource access later.
  identity {
    type = "SystemAssigned"
  }

  secret {
    name  = "jwt-signing-key"
    value = azurerm_key_vault_secret.jwt_signing_key.value
  }
  secret {
    name  = "acr-password"
    value = azurerm_container_registry.acr.admin_password
  }

  registry {
    server               = azurerm_container_registry.acr.login_server
    username             = azurerm_container_registry.acr.admin_username
    password_secret_name = "acr-password"
  }

  template {
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "segue-app"
      image  = "${azurerm_container_registry.acr.login_server}/segue-app:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__FHIRBridgeDb"
        value = local.seguedb_connection_string
      }
      env {
        name  = "Database__Provider"
        value = "PostgreSql"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = local.redis_connection_string
      }
      # Only for the containerized path's self-signed cert — Azure Cache for Redis presents a
      # normal CA-trusted certificate, which ValidateRedisServerCertificate accepts on its own when
      # this is left unset. See the azurerm_redis_cache.main resource's comment above.
      dynamic "env" {
        for_each = var.use_azure_cache_for_redis ? [] : [1]
        content {
          name  = "Redis__TrustedCertificateThumbprint"
          value = var.redis_trusted_certificate_thumbprint
        }
      }
      env {
        name        = "Authentication__SigningKey"
        secret_name = "jwt-signing-key"
      }
      env {
        name  = "DataProtection__KeyRingPath"
        value = "/app/keys"
      }
      # Gateway proxies /api to the Api process in this same container (entrypoint binds Api on loopback :5000).
      env {
        name  = "ApiBaseUrl"
        value = "http://127.0.0.1:5000/"
      }
      env {
        name  = "AllowedHosts"
        value = "*"
      }
      # See enable_tenant_secrets_key_vault's description — only emitted when that flag is set, so a
      # deployment that leaves it false keeps today's local-DB-only secret storage untouched.
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__UseAzureKeyVault"
          value = "true"
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__AllowConfigurationFallback"
          value = "true"
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__VaultName"
          value = azurerm_key_vault.tenant_secrets[0].vault_uri
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "DataProtection__KeyVaultKeyId"
          value = azurerm_key_vault_key.dataprotection[0].id
        }
      }
      # See enable_seq's description — only emitted when that flag is set. FHIRBridge.Api and
      # FHIRBridge.Gateway (this same container) both read this key via SegueLogging.
      dynamic "env" {
        for_each = var.enable_seq ? [1] : []
        content {
          name  = "Observability__SeqServerUrl"
          value = "http://${local.seq_name}"
        }
      }

      volume_mounts {
        name = "keys-data"
        path = "/app/keys"
      }
    }

    volume {
      name         = "keys-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.keys_data.name
    }
  }

  ingress {
    external_enabled = true
    target_port      = 80
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Custom domains (optional, per app) ---
#
# Azure managed certificates require the hostname to already exist on a Container App
# (RequireCustomHostnameInEnvironment otherwise). azurerm_container_app_custom_domain with
# certificate_binding_type=Disabled registers the hostname without a cert (Phase 1) — that's all
# this config does right now.
#
# Phase 2 (managed certs, SniEnabled binding) is currently DISABLED: it needs the
# azurerm_container_app_environment_managed_certificate resource, which was only added in
# terraform-provider-azurerm v4.69.0 — this config is pinned to `~> 3.100` (see the
# required_providers block in this file), and no 3.x release has it. Referencing that resource
# type at all — even behind count = 0 — makes Terraform fail schema validation on EVERY plan/apply/
# destroy against a 3.x provider, not just when a custom domain is actually configured, which is
# why it's removed here rather than left gated behind var.bind_custom_domain_certificates. Setting
# bind_custom_domain_certificates = true currently has no effect (the ternaries below always
# resolve to "Disabled"/null) until this environment is migrated to azurerm ~> 4.69 (a deliberate,
# separate change — 4.x has its own breaking changes across many other resources in this file, so
# don't do it just to unblock a custom domain).

resource "azurerm_container_app_custom_domain" "segue_app" {
  count            = var.segue_app_custom_domain != "" ? 1 : 0
  name             = var.segue_app_custom_domain
  container_app_id = azurerm_container_app.segue_app.id

  certificate_binding_type                 = "Disabled"
  container_app_environment_certificate_id = null
}

# --- Worker (no ingress) ---

resource "azurerm_container_app" "worker" {
  name                         = local.worker_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  # segue_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. Container Apps
  # replaces crashed replicas automatically, which turns a lost race into a self-healing retry.
  # Redis is reached by its plain (predictable) name in the containerized path, so needs an
  # explicit dependency the same as on segue_app; the Postgres connection is inferred
  # automatically via local.seguedb_connection_string's resource reference.
  depends_on = [azurerm_container_app.redis, azurerm_container_app.segue_app]

  # See the identical block on azurerm_container_app.segue_app for why this exists.
  identity {
    type = "SystemAssigned"
  }

  secret {
    name  = "acr-password"
    value = azurerm_container_registry.acr.admin_password
  }

  registry {
    server               = azurerm_container_registry.acr.login_server
    username             = azurerm_container_registry.acr.admin_username
    password_secret_name = "acr-password"
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "worker"
      image  = "${azurerm_container_registry.acr.login_server}/segue-worker:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__FHIRBridgeDb"
        value = local.seguedb_connection_string
      }
      env {
        name  = "Database__Provider"
        value = "PostgreSql"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = local.redis_connection_string
      }
      dynamic "env" {
        for_each = var.use_azure_cache_for_redis ? [] : [1]
        content {
          name  = "Redis__TrustedCertificateThumbprint"
          value = var.redis_trusted_certificate_thumbprint
        }
      }
      env {
        name  = "RuntimeWorker__Enabled"
        value = "true"
      }
      env {
        name  = "Messaging__Provider"
        value = "InMemory"
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__UseAzureKeyVault"
          value = "true"
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__AllowConfigurationFallback"
          value = "true"
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "KeyVault__VaultName"
          value = azurerm_key_vault.tenant_secrets[0].vault_uri
        }
      }
      dynamic "env" {
        for_each = var.enable_tenant_secrets_key_vault ? [1] : []
        content {
          name  = "DataProtection__KeyVaultKeyId"
          value = azurerm_key_vault_key.dataprotection[0].id
        }
      }
      # See enable_seq's description on segue_app above.
      dynamic "env" {
        for_each = var.enable_seq ? [1] : []
        content {
          name  = "Observability__SeqServerUrl"
          value = "http://${local.seq_name}"
        }
      }
    }
  }
}

# --- Resource manifest (fallback for cleanup from a machine without terraform.tfstate) ---
#
# `terraform destroy` never reads this file — it works entirely from Terraform's own state, which
# is what actually tracks what this config created (see the "how does cleanup know what's ours"
# discussion in the containerization guide). This manifest exists purely as a human-readable
# safety net: if terraform.tfstate is ever lost, or someone needs to clean this deployment up from
# a different machine that never had it, this text file lists every resource ID this config
# created, stored inside the one storage account this deployment already owns — so it survives
# independently of any local Terraform state. Referencing every resource's `.id` below also means
# this naturally finishes last in the apply graph, after everything it lists has been created.

resource "azurerm_storage_container" "manifest" {
  name                  = "deployment-manifest"
  storage_account_name  = azurerm_storage_account.main.name
  container_access_type = "private"
}

locals {
  resource_manifest_text = join("\n", [
    "Segue containerization deployment - resource manifest",
    "name_prefix: ${var.name_prefix}",
    "resource_group (pre-existing, NOT managed by this config): ${data.azurerm_resource_group.main.name}",
    "",
    "Every resource ID below WAS created by this Terraform config. terraform destroy (run from a",
    "machine with the matching terraform.tfstate) is always the right way to remove them. If that",
    "state is unavailable, each can instead be deleted directly, e.g.:",
    "  az resource delete --ids \"<id>\"",
    "",
    "azurerm_container_registry.acr                       = ${azurerm_container_registry.acr.id}",
    "azurerm_log_analytics_workspace.main                 = ${azurerm_log_analytics_workspace.main.id}",
    "azurerm_container_app_environment.main                = ${azurerm_container_app_environment.main.id}",
    "azurerm_storage_account.main                          = ${azurerm_storage_account.main.id}",
    "azurerm_storage_share.keys_data                       = ${azurerm_storage_share.keys_data.id}",
    "azurerm_container_app_environment_storage.keys_data   = ${azurerm_container_app_environment_storage.keys_data.id}",
    "azurerm_key_vault.main                                = ${azurerm_key_vault.main.id}",
    "azurerm_key_vault_access_policy.terraform_kv_secrets  = ${azurerm_key_vault_access_policy.terraform_kv_secrets.id}",
    "azurerm_key_vault_secret.jwt_signing_key              = ${azurerm_key_vault_secret.jwt_signing_key.id}",
    "azurerm_container_app.segue_app                  = ${azurerm_container_app.segue_app.id}",
    "azurerm_container_app.worker                          = ${azurerm_container_app.worker.id}",
    "azurerm_storage_container.manifest (this file's own container) = ${azurerm_storage_container.manifest.id}",
  ])
}

resource "azurerm_storage_blob" "resource_manifest" {
  name                   = "resources.txt"
  storage_account_name   = azurerm_storage_account.main.name
  storage_container_name = azurerm_storage_container.manifest.name
  type                   = "Block"
  source_content         = local.resource_manifest_text
}
