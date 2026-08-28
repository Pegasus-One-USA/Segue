# Reproduces the same 5-container topology as containerization/compose/docker-compose.yml, plus a
# HAPI terminology server + its own Postgres (2 more containers, not currently in that compose
# file — see the note on docker_container.hapi_terminology below), via Terraform, so
# `terraform apply` alone can also stand up the local stack (the compose file remains available as
# the faster manual path for the original 5). Assumes the 3 custom images have already been built
# with containerization/scripts/build-images.sh|ps1 (this environment does not build images itself).

terraform {
  required_version = ">= 1.6.0"
  required_providers {
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }
}

provider "docker" {}

locals {
  # Docker's equivalent of cloud resource tags. Applied to the network, volumes, and every
  # container below via the repeated `dynamic "labels"` block — lets `docker ps/volume ls/network
  # ls --filter label=...` (and the cleanup script) find everything this stack created.
  common_labels = {
    "com.fhirbridge.project"     = "FHIRBridge"
    "com.fhirbridge.component"   = "containerization"
    "com.fhirbridge.environment" = "local"
    "com.fhirbridge.managed-by"  = "Terraform"
  }
}

resource "docker_network" "fhirbridge" {
  name = "fhirbridge-containerized"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "sqlserver_data" {
  name = "fhirbridge-ctr-sqlserver-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "redis_data" {
  name = "fhirbridge-ctr-redis-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "hapi_terminology_postgres_data" {
  name = "fhirbridge-ctr-hapi-terminology-postgres-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "dataprotection_keys" {
  name = "fhirbridge-ctr-dataprotection-keys"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

# --- SQL Server Express ---

resource "docker_image" "sqlserver" {
  name = "mcr.microsoft.com/mssql/server:2022-latest"
}

resource "docker_container" "sqlserver" {
  name  = "fhirbridge-ctr-sqlserver"
  image = docker_image.sqlserver.image_id

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["sqlserver"]
  }

  env = [
    "ACCEPT_EULA=Y",
    "MSSQL_PID=Express",
    "MSSQL_SA_PASSWORD=${var.sql_sa_password}",
  ]

  ports {
    internal = 1433
    external = var.sql_host_port
  }

  volumes {
    volume_name    = docker_volume.sqlserver_data.name
    container_path = "/var/opt/mssql"
  }

  healthcheck {
    test     = ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"${var.sql_sa_password}\" -C -Q 'SELECT 1' || exit 1"]
    interval = "10s"
    timeout  = "5s"
    retries  = 10
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

# --- Redis ---
#
# Custom, locally-built image (not stock redis:7-alpine, and not a docker_image resource that
# pulls from a registry — same pattern as fhirbridge-app/demo-app/worker below): FHIRBridge.Api/
# .Worker refuse a plaintext Redis connection outside Development (HIPAA #15), and stock Redis has
# no TLS configured at all. Build it with the others via build-images.ps1/.sh before `terraform
# apply` — see containerization/docker/redis-tls/Dockerfile.

resource "docker_container" "redis" {
  name  = "fhirbridge-ctr-redis"
  image = "fhirbridge-redis:${var.image_tag}"
  # Relying solely on network isolation isn't defense-in-depth — requirepass means a compromised
  # container elsewhere on this network still can't just connect to Redis without the password.
  # --port 0 disables the plaintext port entirely — --tls-port is the only one Redis listens on.
  # --tls-auth-clients no means server-side TLS + --requirepass, not mutual TLS (no client
  # certificate required) — matches ConnectionStrings__Redis's "ssl=true" (no client cert options)
  # on fhirbridge-app/worker below.
  command = [
    "redis-server",
    "--tls-port", "6379",
    "--port", "0",
    "--tls-cert-file", "/certs/redis.crt",
    "--tls-key-file", "/certs/redis.key",
    "--tls-auth-clients", "no",
    "--requirepass", var.redis_password,
  ]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["redis"]
  }

  ports {
    internal = 6379
    external = var.redis_host_port
  }

  volumes {
    volume_name    = docker_volume.redis_data.name
    container_path = "/data"
  }

  healthcheck {
    # --tls --insecure: Redis only listens on TLS now (--port 0 above); --insecure skips
    # certificate validation for this healthcheck specifically (not a security-relevant check —
    # the app's own connection is what validates the certificate, per DependencyInjection.cs).
    test     = ["CMD", "redis-cli", "--tls", "--insecure", "-a", var.redis_password, "ping"]
    interval = "10s"
    timeout  = "5s"
    retries  = 10
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

# --- HAPI terminology server + its own Postgres ---
#
# A second, dedicated HAPI FHIR instance used only for code-system lookups/validation/expansion/
# translation ($lookup et al.) and the automatic vocabulary syncs in
# FHIRBridge.Infrastructure/Terminology/Hapi — separate from any EHR-sourced FHIR data. Not part of
# containerization/compose/docker-compose.yml today (that lean 5-container file is a separate,
# independent stack) — only reachable here via Terraform, or from the repo-root dev/E2E
# docker-compose.yml's own hapi-terminology/hapi-terminology-postgres pair.

resource "docker_image" "hapi_terminology_postgres" {
  name = "postgres:16-alpine"
}

resource "docker_container" "hapi_terminology_postgres" {
  name  = "fhirbridge-ctr-hapi-terminology-postgres"
  image = docker_image.hapi_terminology_postgres.image_id

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["hapi-terminology-postgres"]
  }

  env = [
    "POSTGRES_DB=hapi_terminology",
    "POSTGRES_USER=hapi_terminology",
    "POSTGRES_PASSWORD=${var.hapi_terminology_postgres_password}",
  ]

  volumes {
    volume_name    = docker_volume.hapi_terminology_postgres_data.name
    container_path = "/var/lib/postgresql/data"
  }

  healthcheck {
    test     = ["CMD-SHELL", "pg_isready -U hapi_terminology -d hapi_terminology"]
    interval = "10s"
    timeout  = "5s"
    retries  = 10
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

resource "docker_image" "hapi_terminology" {
  name = "hapiproject/hapi:latest"
}

resource "docker_container" "hapi_terminology" {
  name  = "fhirbridge-ctr-hapi-terminology"
  image = docker_image.hapi_terminology.image_id

  depends_on = [docker_container.hapi_terminology_postgres]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["hapi-terminology"]
  }

  env = [
    "SPRING_DATASOURCE_URL=jdbc:postgresql://hapi-terminology-postgres:5432/hapi_terminology",
    "SPRING_DATASOURCE_USERNAME=hapi_terminology",
    "SPRING_DATASOURCE_PASSWORD=${var.hapi_terminology_postgres_password}",
    "SPRING_DATASOURCE_DRIVERCLASSNAME=org.postgresql.Driver",
    "SPRING_JPA_PROPERTIES_HIBERNATE_DIALECT=ca.uhn.fhir.jpa.model.dialect.HapiFhirPostgres94Dialect",
    "HAPI_FHIR_VERSION=R4",
  ]

  ports {
    internal = 8080
    external = var.hapi_terminology_host_port
  }

  healthcheck {
    test     = ["CMD", "java", "-version"]
    interval = "20s"
    timeout  = "10s"
    retries  = 15
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

# --- FHIRBridge app (Api + Gateway) ---

resource "docker_container" "fhirbridge_app" {
  name  = "fhirbridge-ctr-app"
  image = "fhirbridge-app:${var.image_tag}"

  depends_on = [docker_container.sqlserver, docker_container.redis, docker_container.hapi_terminology]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["fhirbridge-app"]
  }

  env = [
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Server=sqlserver,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True",
    "ConnectionStrings__Redis=redis:6379,password=${var.redis_password},ssl=true",
    "Authentication__SigningKey=${var.jwt_signing_key}",
    "DataProtection__KeyRingPath=/app/keys",
    "Portal__AllowedOrigins__0=http://localhost:${var.demo_host_port}",
    "AllowedHosts=*",
    # Api and Gateway are sibling processes in one container (entrypoint.sh) - Api binds
    # loopback-only on 5000, and Gateway throws at startup outside Development without this.
    "ApiBaseUrl=http://127.0.0.1:5000/",
    "Terminology__BaseUrl=http://hapi-terminology:8080/fhir",
  ]

  ports {
    internal = 80
    external = var.app_host_port
  }

  volumes {
    volume_name    = docker_volume.dataprotection_keys.name
    container_path = "/app/keys"
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

# --- Demo app (self-hosts its own frontend) ---

resource "docker_container" "demo_app" {
  name  = "fhirbridge-ctr-demo-app"
  image = "demo-app:${var.image_tag}"

  depends_on = [docker_container.sqlserver]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["demo-app"]
  }

  env = [
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__Default=Server=sqlserver,1433;Database=HealthAppDb;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True",
    "AllowedFrontendOrigin=http://localhost:${var.demo_host_port}",
  ]

  ports {
    internal = 5500
    external = var.demo_host_port
  }

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}

# --- Worker (no HTTP endpoint) ---

resource "docker_container" "worker" {
  name  = "fhirbridge-ctr-worker"
  image = "fhirbridge-worker:${var.image_tag}"

  # fhirbridge_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. restart="unless-stopped"
  # above is the actual safety net that turns a lost race into a self-healing retry.
  depends_on = [docker_container.sqlserver, docker_container.redis, docker_container.hapi_terminology, docker_container.fhirbridge_app]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["worker"]
  }

  env = [
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Server=sqlserver,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};Encrypt=True;TrustServerCertificate=True",
    "ConnectionStrings__Redis=redis:6379,password=${var.redis_password},ssl=true",
    "RuntimeWorker__Enabled=true",
    "Messaging__Provider=InMemory",
    "Terminology__BaseUrl=http://hapi-terminology:8080/fhir",
  ]

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}
