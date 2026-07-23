# Reproduces the same 5-container topology as containerization/compose/docker-compose.yml, via
# Terraform, so `terraform apply` alone can also stand up the local stack (the compose file remains
# available as the faster manual path). Assumes the 3 custom images have already been built with
# containerization/scripts/build-images.sh|ps1 (this environment does not build images itself).

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

resource "docker_network" "fhirbridge" {
  name = "fhirbridge-containerized"
}

resource "docker_volume" "sqlserver_data" {
  name = "fhirbridge-ctr-sqlserver-data"
}

resource "docker_volume" "redis_data" {
  name = "fhirbridge-ctr-redis-data"
}

resource "docker_volume" "dataprotection_keys" {
  name = "fhirbridge-ctr-dataprotection-keys"
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

  restart = "unless-stopped"
}

# --- Redis ---

resource "docker_image" "redis" {
  name = "redis:7-alpine"
}

resource "docker_container" "redis" {
  name  = "fhirbridge-ctr-redis"
  image = docker_image.redis.image_id

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
    test     = ["CMD", "redis-cli", "ping"]
    interval = "10s"
    timeout  = "5s"
    retries  = 10
  }

  restart = "unless-stopped"
}

# --- FHIRBridge app (Api + Gateway) ---

resource "docker_container" "fhirbridge_app" {
  name  = "fhirbridge-ctr-app"
  image = "fhirbridge-app:${var.image_tag}"

  depends_on = [docker_container.sqlserver, docker_container.redis]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["fhirbridge-app"]
  }

  env = [
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Server=sqlserver,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True",
    "ConnectionStrings__Redis=redis:6379",
    "Authentication__SigningKey=${var.jwt_signing_key}",
    "DataProtection__KeyRingPath=/app/keys",
    "Portal__AllowedOrigins__0=http://localhost:${var.demo_host_port}",
    "AllowedHosts=*",
  ]

  ports {
    internal = 80
    external = var.app_host_port
  }

  volumes {
    volume_name    = docker_volume.dataprotection_keys.name
    container_path = "/app/keys"
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
    "ConnectionStrings__Default=Server=sqlserver,1433;Database=HealthAppDb;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True",
    "AllowedFrontendOrigin=http://localhost:${var.demo_host_port}",
  ]

  ports {
    internal = 5500
    external = var.demo_host_port
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
  depends_on = [docker_container.sqlserver, docker_container.redis, docker_container.fhirbridge_app]

  networks_advanced {
    name    = docker_network.fhirbridge.name
    aliases = ["worker"]
  }

  env = [
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Server=sqlserver,1433;Database=FHIRBridge;User Id=sa;Password=${var.sql_sa_password};TrustServerCertificate=True",
    "ConnectionStrings__Redis=redis:6379",
    "RuntimeWorker__Enabled=true",
    "Messaging__Provider=InMemory",
  ]

  restart = "unless-stopped"
}
