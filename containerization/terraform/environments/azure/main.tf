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
  }
}

provider "azurerm" {
  features {}
}

resource "random_id" "suffix" {
  byte_length = 3
}

locals {
  suffix               = random_id.suffix.hex
  resource_group_name  = "${var.name_prefix}-rg"
  acr_name             = "${var.name_prefix}acr${local.suffix}" # ACR: alnum only, globally unique
  storage_account_name = "${var.name_prefix}st${local.suffix}"  # Storage account: alnum only, <=24 chars, globally unique

  # Plain-string app names (not resource attribute lookups) so a Container App can safely compute
  # its OWN public URL — Container Apps' FQDN is always "<app-name>.<environment-default-domain>",
  # and the environment's default_domain doesn't depend on any individual app, so this avoids the
  # self-reference a resource would otherwise need to read its own computed attributes.
  sqlserver_name      = "${var.name_prefix}-sqlserver"
  redis_name          = "${var.name_prefix}-redis"
  fhirbridge_app_name = "${var.name_prefix}-app"
  demo_app_name       = "${var.name_prefix}-demo-app"
  worker_name         = "${var.name_prefix}-worker"
}

resource "azurerm_resource_group" "main" {
  name     = local.resource_group_name
  location = var.location
}

# --- Container registry (custom images are pushed here by the build script) ---

resource "azurerm_container_registry" "acr" {
  name                = local.acr_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = true
}

# --- Container Apps environment ---

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${var.name_prefix}-logs"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_container_app_environment" "main" {
  name                       = "${var.name_prefix}-env"
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
}

# --- Persistent storage for SQL Server + Redis (Container Apps are otherwise stateless) ---

resource "azurerm_storage_account" "main" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
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

# --- SQL Server Express (internal only, single replica) ---

resource "azurerm_container_app" "sqlserver" {
  name                         = local.sqlserver_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  secret {
    name  = "sql-sa-password"
    value = var.sql_sa_password
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

      volume_mounts {
        name = "sql-data"
        path = "/var/opt/mssql"
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 1433
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
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

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

      volume_mounts {
        name = "redis-data"
        path = "/data"
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 6379
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
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  # Connection strings reference sqlserver/redis by their plain (predictable) name rather than a
  # resource attribute, so this dependency has to be spelled out explicitly.
  depends_on = [azurerm_container_app.sqlserver, azurerm_container_app.redis]

  secret {
    name  = "sql-sa-password"
    value = var.sql_sa_password
  }
  secret {
    name  = "jwt-signing-key"
    value = var.jwt_signing_key
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
      name   = "fhirbridge-app"
      image  = "${azurerm_container_registry.acr.login_server}/fhirbridge-app:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__FHIRBridgeDb"
        value = "Server=${local.sqlserver_name},1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "${local.redis_name}:6379"
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
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  depends_on = [azurerm_container_app.sqlserver]

  secret {
    name  = "sql-sa-password"
    value = var.sql_sa_password
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
        value = "Server=${local.sqlserver_name},1433;Database=HealthAppDb;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True"
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

# --- Worker (no ingress) ---

resource "azurerm_container_app" "worker" {
  name                         = local.worker_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  # fhirbridge_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. Container Apps
  # replaces crashed replicas automatically, which turns a lost race into a self-healing retry.
  depends_on = [azurerm_container_app.sqlserver, azurerm_container_app.redis, azurerm_container_app.fhirbridge_app]

  secret {
    name  = "sql-sa-password"
    value = var.sql_sa_password
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
        value = "Server=${local.sqlserver_name},1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "${local.redis_name}:6379"
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
