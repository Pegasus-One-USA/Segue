# Reproduces a 4-container topology (Postgres, Redis, segue-app, worker) via Terraform, so
# `terraform apply` alone can also stand up the local stack. This environment used to carry two
# extra containers containerization/compose/docker-compose.yml never had, on top of the same
# container set compose runs, including one application container compose still runs today — both
# extras were removed here to bring this Terraform path in line with the azure/aws/bicep
# environments, which made the same two removals and also moved Segue's own database off SQL
# Server onto Postgres. Net effect: this is now a smaller, different container set than compose's
# own — compose itself is untouched and still runs its original containers unchanged; it remains
# the faster manual path if that specific shape is what's needed. Assumes the 3 custom images have
# already been built with containerization/scripts/build-images.sh|ps1 (this environment does not
# build images itself).

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
    "com.segue.project"     = "Segue"
    "com.segue.component"   = "containerization"
    "com.segue.environment" = "local"
    "com.segue.managed-by"  = "Terraform"
  }
}

resource "docker_network" "segue" {
  name = "segue-containerized"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "postgres_data" {
  name = "segue-ctr-postgres-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "redis_data" {
  name = "segue-ctr-redis-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

resource "docker_volume" "dataprotection_keys" {
  name = "segue-ctr-dataprotection-keys"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

# Only needed when var.enable_seq is true.
resource "docker_volume" "seq_data" {
  count = var.enable_seq ? 1 : 0
  name  = "segue-ctr-seq-data"

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }
}

# --- Postgres (Segue's own database) ---
#
# Stock postgres:16-alpine, replacing the SQL Server Express container this environment used before
# the app migrated from SQL Server to PostgreSQL — matches the containerized-Postgres path in the
# azure/aws environments (there's no "managed PaaS Postgres" toggle here: this environment has no
# cloud provider at all, so a containerized Postgres is the only option). PGDATA below points at a
# subdirectory of the mount rather than the mount root itself — on the azure/aws environments this
# works around Azure Files/EFS not supporting the chown/chmod postgres's entrypoint does on PGDATA
# at first boot; a plain Docker named volume like docker_volume.postgres_data has no such
# restriction, but keeping the same env var here anyway means one less thing to explain differently
# per environment, and it's harmless either way.

resource "docker_image" "postgres" {
  name = "postgres:16-alpine"
}

resource "docker_container" "postgres" {
  name  = "segue-ctr-postgres"
  image = docker_image.postgres.image_id

  networks_advanced {
    name    = docker_network.segue.name
    aliases = ["postgres"]
  }

  env = [
    "POSTGRES_DB=Segue",
    "POSTGRES_USER=segue",
    "POSTGRES_PASSWORD=${var.postgres_password}",
    "PGDATA=/var/lib/postgresql/data/pgdata",
  ]

  ports {
    internal = 5432
    external = var.postgres_host_port
  }

  volumes {
    volume_name    = docker_volume.postgres_data.name
    container_path = "/var/lib/postgresql/data"
  }

  healthcheck {
    test     = ["CMD-SHELL", "pg_isready -U segue -d Segue"]
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
# pulls from a registry — same pattern as segue-app/worker below): FHIRBridge.Api/
# .Worker refuse a plaintext Redis connection outside Development (HIPAA #15), and stock Redis has
# no TLS configured at all. Build it with the others via build-images.ps1/.sh before `terraform
# apply` — see containerization/docker/redis-tls/Dockerfile.

resource "docker_container" "redis" {
  name  = "segue-ctr-redis"
  image = "segue-redis:${var.image_tag}"
  # Relying solely on network isolation isn't defense-in-depth — requirepass means a compromised
  # container elsewhere on this network still can't just connect to Redis without the password.
  # --port 0 disables the plaintext port entirely — --tls-port is the only one Redis listens on.
  # --tls-auth-clients no means server-side TLS + --requirepass, not mutual TLS (no client
  # certificate required) — matches ConnectionStrings__Redis's "ssl=true" (no client cert options)
  # on segue-app/worker below.
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
    name    = docker_network.segue.name
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

# --- Seq (structured log viewing) — only when var.enable_seq is true ---

resource "docker_image" "seq" {
  count = var.enable_seq ? 1 : 0
  name  = "datalust/seq:latest"
}

resource "docker_container" "seq" {
  count = var.enable_seq ? 1 : 0
  name  = "segue-ctr-seq"
  image = docker_image.seq[0].image_id

  networks_advanced {
    name    = docker_network.segue.name
    aliases = ["seq"]
  }

  env = [
    "ACCEPT_EULA=Y",
    "SEQ_FIRSTRUN_ADMINPASSWORD=${var.seq_admin_password}",
  ]

  ports {
    internal = 80
    external = var.seq_host_port
  }

  volumes {
    volume_name    = docker_volume.seq_data[0].name
    container_path = "/data"
  }

  healthcheck {
    test     = ["CMD-SHELL", "curl -f http://localhost/api || exit 1"]
    interval = "15s"
    timeout  = "10s"
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

# --- Segue app (Api + Gateway) ---

resource "docker_container" "segue_app" {
  name  = "segue-ctr-app"
  image = "segue-app:${var.image_tag}"

  depends_on = [docker_container.postgres, docker_container.redis, docker_container.seq]

  networks_advanced {
    name    = docker_network.segue.name
    aliases = ["segue-app"]
  }

  env = concat([
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Host=postgres;Port=5432;Database=Segue;Username=segue;Password=${var.postgres_password};",
    "Database__Provider=PostgreSql",
    "ConnectionStrings__Redis=redis:6379,password=${var.redis_password},ssl=true",
    "Redis__TrustedCertificateThumbprint=${var.redis_trusted_certificate_thumbprint}",
    "Authentication__SigningKey=${var.jwt_signing_key}",
    "DataProtection__KeyRingPath=/app/keys",
    "AllowedHosts=*",
    # Api and Gateway are sibling processes in one container (entrypoint.sh) - Api binds
    # loopback-only on 5000, and Gateway throws at startup outside Development without this.
    "ApiBaseUrl=http://127.0.0.1:5000/",
    ],
    # See enable_seq's description in variables.tf. Both Api and Gateway (this same container)
    # read this key via SegueLogging.
    var.enable_seq ? ["Observability__SeqServerUrl=http://seq"] : []
  )

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

# --- Worker (no HTTP endpoint) ---

resource "docker_container" "worker" {
  name  = "segue-ctr-worker"
  image = "segue-worker:${var.image_tag}"

  # segue_app is a head start, not a guarantee: both it and worker auto-migrate FHIRBridgeDb
  # on boot and can race on the initial CREATE DATABASE on a fresh database. restart="unless-stopped"
  # above is the actual safety net that turns a lost race into a self-healing retry.
  depends_on = [docker_container.postgres, docker_container.redis, docker_container.seq, docker_container.segue_app]

  networks_advanced {
    name    = docker_network.segue.name
    aliases = ["worker"]
  }

  env = concat([
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__FHIRBridgeDb=Host=postgres;Port=5432;Database=Segue;Username=segue;Password=${var.postgres_password};",
    "Database__Provider=PostgreSql",
    "ConnectionStrings__Redis=redis:6379,password=${var.redis_password},ssl=true",
    "Redis__TrustedCertificateThumbprint=${var.redis_trusted_certificate_thumbprint}",
    "RuntimeWorker__Enabled=true",
    "Messaging__Provider=InMemory",
    ],
    # See enable_seq's description on segue_app above.
    var.enable_seq ? ["Observability__SeqServerUrl=http://seq"] : []
  )

  dynamic "labels" {
    for_each = local.common_labels
    content {
      label = labels.key
      value = labels.value
    }
  }

  restart = "unless-stopped"
}
