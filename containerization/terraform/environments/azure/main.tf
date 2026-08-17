# Deploys the same 5-container topology to Azure Container Apps. Terraform only deploys — it does
# NOT build images. Before `terraform apply`, run:
#
#   ../../../scripts/build-images.sh -r <acr-login-server> -t <image_tag> -p
#
# against the ACR this config creates (its login server is in the `acr_login_server` output —
# create the ACR first with a targeted apply, or push after the first apply and re-apply to update
# the Container Apps' image references).
#
# SQL Server Express and Redis are pinned to a single replica each (Azure Files-backed data
# directories are not safe for concurrent multi-instance SQL Server / Redis processes) and are
# reachable by other Container Apps in the same environment via their app name as hostname
# (Container Apps' built-in internal DNS) — never exposed externally.

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
    # azurerm (3.117.1, as pinned below) has no resource for Azure Container Apps' free managed-
    # certificate feature — azapi is Microsoft's own official provider that creates any ARM
    # resource type 1:1 (type + apiVersion + body, same shape as an ARM/Bicep template), used here
    # only for that one gap. Everything else stays on azurerm.
    azapi = {
      source  = "Azure/azapi"
      version = ">= 1.9.0"
    }
  }
}

provider "azapi" {}

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

locals {
  suffix               = random_id.suffix.hex
  acr_name             = "${var.name_prefix}acr${local.suffix}"             # ACR: alnum only, globally unique
  storage_account_name = "${var.name_prefix}st${local.suffix}"              # Storage account: alnum only, <=24 chars, globally unique
  key_vault_name       = "${var.name_prefix}-kv-${random_id.kv_suffix.hex}" # Key Vault: alnum + hyphens, <=24 chars, globally unique, own random component

  # Plain-string app names (not resource attribute lookups) so a Container App can safely compute
  # its OWN public URL — Container Apps' FQDN is always "<app-name>.<environment-default-domain>",
  # and the environment's default_domain doesn't depend on any individual app, so this avoids the
  # self-reference a resource would otherwise need to read its own computed attributes.
  sqlserver_name      = "${var.name_prefix}-sqlserver"
  redis_name          = "${var.name_prefix}-redis"
  fhirbridge_app_name = "${var.name_prefix}-app"
  demo_app_name       = "${var.name_prefix}-demo-app"
  worker_name         = "${var.name_prefix}-worker"

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

# --- Persistent storage for SQL Server + Redis (Container Apps are otherwise stateless) ---

resource "azurerm_storage_account" "main" {
  name                     = local.storage_account_name
  resource_group_name      = data.azurerm_resource_group.main.name
  location                 = data.azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  tags                     = local.common_tags
}

resource "azurerm_storage_share" "sql_data" {
  name                 = "sql-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 50
}

resource "azurerm_storage_share" "redis_data" {
  name                 = "redis-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 10
}

# ASP.NET Core's Data Protection key ring for fhirbridge_app (see DataProtection__KeyRingPath
# below) — without this, /app/keys is ephemeral per-container storage: every restart (redeploy,
# scale event, platform maintenance, anything) generates a brand new key ring, permanently
# orphaning whatever's already encrypted in FHIRBridgeDb's ProvisionedSecrets table (jwt-signing-
# key, download-link-signing-secret — see AppSecretProvisioner) with a CryptographicException
# ("key ... was not found in the key ring") that crashes the Api process on every single boot from
# then on. Unlike sql-data/redis-data above, concurrent multi-instance access to a shared key ring
# directory is an explicitly supported ASP.NET Core Data Protection pattern (not a SQL Server-style
# single-writer constraint), so this is also what makes fhirbridge_app's max_replicas = 3 actually
# safe — without it, scaling out would hit this exact same exception between sibling replicas that
# each generated their own independent key ring.
resource "azurerm_storage_share" "keys_data" {
  name                 = "keys-data"
  storage_account_name = azurerm_storage_account.main.name
  quota                = 1
}

resource "azurerm_container_app_environment_storage" "sql_data" {
  name                         = "sql-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.sql_data.name
  access_mode                  = "ReadWrite"
}

resource "azurerm_container_app_environment_storage" "redis_data" {
  name                         = "redis-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.redis_data.name
  access_mode                  = "ReadWrite"
}

resource "azurerm_container_app_environment_storage" "keys_data" {
  name                         = "keys-data"
  container_app_environment_id = azurerm_container_app_environment.main.id
  account_name                 = azurerm_storage_account.main.name
  access_key                   = azurerm_storage_account.main.primary_access_key
  share_name                   = azurerm_storage_share.keys_data.name
  access_mode                  = "ReadWrite"
}

# --- Secrets (Key Vault is the source of truth; Terraform variables only seed it) ---
#
# The sql_sa_password/jwt_signing_key/redis_password Terraform variables still exist as the
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

resource "azurerm_key_vault_secret" "sql_sa_password" {
  name         = "sql-sa-password"
  value        = var.sql_sa_password
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

resource "azurerm_key_vault_secret" "redis_password" {
  name         = "redis-password"
  value        = var.redis_password
  key_vault_id = azurerm_key_vault.main.id
  tags         = local.common_tags
  depends_on   = [azurerm_key_vault_access_policy.terraform_kv_secrets]

  lifecycle {
    ignore_changes = [value]
  }
}

# --- SQL Server Express (internal only, single replica) ---

resource "azurerm_container_app" "sqlserver" {
  name                         = local.sqlserver_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  secret {
    name  = "sql-sa-password"
    value = azurerm_key_vault_secret.sql_sa_password.value
  }

  template {
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "sql-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.sql_data.name
    }

    container {
      name   = "sqlserver"
      image  = "mcr.microsoft.com/mssql/server:2022-latest"
      cpu    = 1.0
      memory = "2Gi"

      env {
        name  = "ACCEPT_EULA"
        value = "Y"
      }
      env {
        name  = "MSSQL_PID"
        value = "Express"
      }
      env {
        name        = "MSSQL_SA_PASSWORD"
        secret_name = "sql-sa-password"
      }
      # Container Apps ingress target_port alone doesn't change what SQL Server itself listens
      # on — this env var does.
      env {
        name  = "MSSQL_TCP_PORT"
        value = tostring(var.sql_port)
      }

      volume_mounts {
        name = "sql-data"
        path = "/var/opt/mssql"
      }
    }
  }

  ingress {
    # See var.sql_external_access's description — defaults to false (internal-only, as this
    # deployment is otherwise built around network isolation). Only ever set to true deliberately,
    # for a temporary connectivity check (e.g. connecting with SSMS), then revert and re-apply.
    external_enabled = var.sql_external_access
    target_port      = var.sql_port
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Redis (internal only, single replica) ---

resource "azurerm_container_app" "redis" {
  name                         = local.redis_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  template {
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "redis-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.redis_data.name
    }

    container {
      name   = "redis"
      image  = "redis:7-alpine"
      cpu    = 0.5
      memory = "1Gi"
      # Redis has no env-var port/password override — this command override tells the redis-server
      # process itself to listen on var.redis_port and require var.redis_password, matching the
      # ingress target_port below (requirepass is defense-in-depth on top of network isolation).
      command = ["redis-server", "--port", tostring(var.redis_port), "--requirepass", azurerm_key_vault_secret.redis_password.value]

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

# --- FHIRBridge app (Api + Gateway), public ---

resource "azurerm_container_app" "fhirbridge_app" {
  name                         = local.fhirbridge_app_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  # Connection strings reference sqlserver/redis by their plain (predictable) name rather than a
  # resource attribute, so this dependency has to be spelled out explicitly.
  depends_on = [azurerm_container_app.sqlserver, azurerm_container_app.redis]

  secret {
    name  = "sql-sa-password"
    value = azurerm_key_vault_secret.sql_sa_password.value
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

    volume {
      name         = "keys-data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.keys_data.name
    }

    container {
      # Display name only, shown in the Portal's container list — the image reference below is a
      # separate string and still has to match the real, already-published repo name in the
      # registry (fhirbridge-app), which isn't being renamed as part of this pass.
      name   = "segue-app"
      image  = "${azurerm_container_registry.acr.login_server}/fhirbridge-app:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      volume_mounts {
        name = "keys-data"
        path = "/app/keys"
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__FHIRBridgeDb"
        value = "Server=${local.sqlserver_name},${var.sql_port};Database=Segue;User Id=sa;Password=${azurerm_key_vault_secret.sql_sa_password.value};Encrypt=True;TrustServerCertificate=True"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "${local.redis_name}:${var.redis_port},password=${azurerm_key_vault_secret.redis_password.value}"
      }
      env {
        name        = "Authentication__SigningKey"
        secret_name = "jwt-signing-key"
      }
      env {
        name  = "DataProtection__KeyRingPath"
        value = "/app/keys"
      }
      # Demo app is a separate origin whose frontend calls this API cross-origin. Computed from
      # the demo app's own (plain-string) name + the environment's default domain — a Container
      # App's FQDN is always predictable this way, so this needs no second `apply`.
      env {
        name  = "Portal__AllowedOrigins__0"
        value = "https://${local.demo_app_name}.${azurerm_container_app_environment.main.default_domain}"
      }
      # Only emitted once demo_app_custom_domain is actually set — otherwise the demo app only
      # ever calls from its default *.azurecontainerapps.io origin, which __0 above already covers.
      # Without this, binding a custom domain to the demo app would silently break its own calls
      # into this API with a CORS rejection, since its new origin wouldn't be on the allowlist.
      dynamic "env" {
        for_each = var.demo_app_custom_domain != "" ? [1] : []
        content {
          name  = "Portal__AllowedOrigins__1"
          value = "https://${var.demo_app_custom_domain}"
        }
      }
      env {
        name  = "AllowedHosts"
        value = "*"
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

# --- Demo app (self-hosts its own frontend), public ---

resource "azurerm_container_app" "demo_app" {
  name                         = local.demo_app_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  depends_on = [azurerm_container_app.sqlserver]

  secret {
    name  = "sql-sa-password"
    value = azurerm_key_vault_secret.sql_sa_password.value
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
      name   = "demo-app"
      image  = "${azurerm_container_registry.acr.login_server}/demo-app:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__Default"
        value = "Server=${local.sqlserver_name},${var.sql_port};Database=HealthAppDb;User Id=sa;Password=${azurerm_key_vault_secret.sql_sa_password.value};Encrypt=True;TrustServerCertificate=True"
      }
      # This app's own public FQDN — computed from its own plain-string name + the environment's
      # default domain, not a self-reference to this resource's computed attributes.
      env {
        name  = "AllowedFrontendOrigin"
        value = "https://${local.demo_app_name}.${azurerm_container_app_environment.main.default_domain}"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 5500
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

# --- Custom domains (optional, per app) ---
#
# A genuine 3-step flow, not 2 — domain registration and certificate issuance are deliberately
# separate applies (see fhirbridge_app_custom_domain / fhirbridge_app_ssl_enabled in variables.tf):
#   1. Both left at their defaults ("" / false) — plain *.azurecontainerapps.io URL, none of the
#      four resources below exist (count = 0), both apps behave exactly as before this feature
#      existed.
#   2. Domain set, ssl_enabled still false — the azapi_update_resource below patches just the
#      customDomains array on the (otherwise azurerm-managed) Container App, bindingType
#      "Disabled", no certificate. The domain resolves (once the client's CNAME points at it) but
#      isn't secured yet.
#   3. ssl_enabled set to true — the azapi_resource managed-certificate request now exists and
#      actually performs the DNS validation at apply time (checks the client's asuid TXT record;
#      fails cleanly if it isn't there yet), and the same azapi_update_resource from step 2 is
#      updated in place to bindingType "SniEnabled" with that certificate's ID.
#
# azapi_update_resource (not azurerm_container_app_custom_domain) is used for the domain
# registration itself because azurerm's own resource currently has an open bug forcing
# container_app_environment_certificate_id to be set even when certificate_binding_type =
# "Disabled" (see hashicorp/terraform-provider-azurerm#24110 / #25788), which would make step 2
# impossible without already having a certificate — defeating the point of splitting these into
# separate steps. azapi_update_resource PATCHes only the
# `properties.configuration.ingress.customDomains` array on the container app azurerm_container_app
# already manages; azurerm's own schema never reads/writes that array itself (that's exactly why a
# separate resource was needed for it in the first place), so the two don't fight over the same
# field on subsequent plans.
#
# CAVEAT — unverified against a live subscription, same as the managed-certificate resource below.
# Also: azapi_update_resource's delete is a no-op against the API (there's no inverse PATCH to "undo" a
# property change) — clearing fhirbridge_app_custom_domain/demo_app_custom_domain back to blank and
# re-applying will NOT remove the domain from the live Container App; that needs a manual
# `az containerapp hostname delete --hostname <domain> -g <rg> -n <app>` afterward.

locals {
  fhirbridge_app_custom_domain_body = var.fhirbridge_app_custom_domain == "" ? null : merge(
    { name = var.fhirbridge_app_custom_domain, bindingType = "Disabled" },
    var.fhirbridge_app_ssl_enabled ? {
      bindingType   = "SniEnabled"
      certificateId = azapi_resource.fhirbridge_app_managed_cert[0].id
    } : {}
  )

  demo_app_custom_domain_body = var.demo_app_custom_domain == "" ? null : merge(
    { name = var.demo_app_custom_domain, bindingType = "Disabled" },
    var.demo_app_ssl_enabled ? {
      bindingType   = "SniEnabled"
      certificateId = azapi_resource.demo_app_managed_cert[0].id
    } : {}
  )
}

# Step 3 only: the managed-certificate request itself. Gated on ssl_enabled (not just the domain
# being set), so step 2 can apply cleanly with no certificate resource in play at all.
resource "azapi_resource" "fhirbridge_app_managed_cert" {
  count     = var.fhirbridge_app_custom_domain != "" && var.fhirbridge_app_ssl_enabled ? 1 : 0
  type      = "Microsoft.App/managedEnvironments/managedCertificates@2024-03-01"
  name      = "${local.fhirbridge_app_name}-cert"
  parent_id = azurerm_container_app_environment.main.id
  location  = data.azurerm_resource_group.main.location
  tags      = local.common_tags
  body = {
    properties = {
      subjectName             = var.fhirbridge_app_custom_domain
      domainControlValidation = "TXT"
    }
  }
}

# Steps 2 and 3 both flow through this single resource — its body just changes shape (Disabled vs
# SniEnabled+certificateId) depending on ssl_enabled, per the locals block above.
resource "azapi_update_resource" "fhirbridge_app_custom_domain" {
  count       = var.fhirbridge_app_custom_domain != "" ? 1 : 0
  resource_id = azurerm_container_app.fhirbridge_app.id
  type        = "Microsoft.App/containerApps@2024-03-01"

  body = {
    properties = {
      configuration = {
        ingress = {
          customDomains = [local.fhirbridge_app_custom_domain_body]
        }
      }
    }
  }
}

resource "azapi_resource" "demo_app_managed_cert" {
  count     = var.demo_app_custom_domain != "" && var.demo_app_ssl_enabled ? 1 : 0
  type      = "Microsoft.App/managedEnvironments/managedCertificates@2024-03-01"
  name      = "${local.demo_app_name}-cert"
  parent_id = azurerm_container_app_environment.main.id
  location  = data.azurerm_resource_group.main.location
  tags      = local.common_tags
  body = {
    properties = {
      subjectName             = var.demo_app_custom_domain
      domainControlValidation = "TXT"
    }
  }
}

resource "azapi_update_resource" "demo_app_custom_domain" {
  count       = var.demo_app_custom_domain != "" ? 1 : 0
  resource_id = azurerm_container_app.demo_app.id
  type        = "Microsoft.App/containerApps@2024-03-01"

  body = {
    properties = {
      configuration = {
        ingress = {
          customDomains = [local.demo_app_custom_domain_body]
        }
      }
    }
  }
}

# --- Worker (no ingress) ---

resource "azurerm_container_app" "worker" {
  name                         = local.worker_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = data.azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.common_tags

  # fhirbridge_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. Container Apps
  # replaces crashed replicas automatically, which turns a lost race into a self-healing retry.
  depends_on = [azurerm_container_app.sqlserver, azurerm_container_app.redis, azurerm_container_app.fhirbridge_app]

  secret {
    name  = "sql-sa-password"
    value = azurerm_key_vault_secret.sql_sa_password.value
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
      image  = "${azurerm_container_registry.acr.login_server}/fhirbridge-worker:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__FHIRBridgeDb"
        value = "Server=${local.sqlserver_name},${var.sql_port};Database=Segue;User Id=sa;Password=${azurerm_key_vault_secret.sql_sa_password.value};Encrypt=True;TrustServerCertificate=True"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "${local.redis_name}:${var.redis_port},password=${azurerm_key_vault_secret.redis_password.value}"
      }
      env {
        name  = "RuntimeWorker__Enabled"
        value = "true"
      }
      env {
        name  = "Messaging__Provider"
        value = "InMemory"
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
    "azurerm_storage_share.sql_data                        = ${azurerm_storage_share.sql_data.id}",
    "azurerm_storage_share.redis_data                      = ${azurerm_storage_share.redis_data.id}",
    "azurerm_storage_share.keys_data                       = ${azurerm_storage_share.keys_data.id}",
    "azurerm_container_app_environment_storage.sql_data    = ${azurerm_container_app_environment_storage.sql_data.id}",
    "azurerm_container_app_environment_storage.redis_data  = ${azurerm_container_app_environment_storage.redis_data.id}",
    "azurerm_container_app_environment_storage.keys_data   = ${azurerm_container_app_environment_storage.keys_data.id}",
    "azurerm_key_vault.main                                = ${azurerm_key_vault.main.id}",
    "azurerm_key_vault_access_policy.terraform_kv_secrets  = ${azurerm_key_vault_access_policy.terraform_kv_secrets.id}",
    "azurerm_key_vault_secret.sql_sa_password              = ${azurerm_key_vault_secret.sql_sa_password.id}",
    "azurerm_key_vault_secret.jwt_signing_key              = ${azurerm_key_vault_secret.jwt_signing_key.id}",
    "azurerm_key_vault_secret.redis_password               = ${azurerm_key_vault_secret.redis_password.id}",
    "azurerm_container_app.sqlserver                       = ${azurerm_container_app.sqlserver.id}",
    "azurerm_container_app.redis                           = ${azurerm_container_app.redis.id}",
    "azurerm_container_app.fhirbridge_app                  = ${azurerm_container_app.fhirbridge_app.id}",
    "azurerm_container_app.demo_app                        = ${azurerm_container_app.demo_app.id}",
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
