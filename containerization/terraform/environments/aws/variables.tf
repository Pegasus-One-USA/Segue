variable "name_prefix" {
  description = "Short name used to build resource names (VPC, cluster, ECR repos, ALB, etc.)."
  type        = string
  default     = "segue"
}

variable "aws_region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "vpc_cidr" {
  description = "CIDR block for the dedicated VPC this stack provisions."
  type        = string
  default     = "10.90.0.0/16"
}

variable "image_tag" {
  description = "Tag the 3 custom images were pushed to ECR with (containerization/scripts/build-images.sh|ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -Tag <this> -Push)."
  type        = string
  default     = "latest"
}

variable "use_rds_postgresql" {
  description = "Chooses which Postgres Segue's own database (FHIRBridgeDb) gets. false (default) keeps a containerized Postgres (aws_ecs_task_definition.postgres — stock postgres:16-alpine, EFS-backed persistence, single task, internal-only via Cloud Map, requires postgres_password). true creates a managed Amazon RDS for PostgreSQL instance instead (aws_db_instance.postgresql — see rds_postgresql_instance_class/rds_postgresql_allocated_storage) and points ConnectionStrings:FHIRBridgeDb at it over a required SSL connection — no container, no EFS volume; AWS manages patching/backups. This is a standard aws_db_instance (engine = \"postgres\"), not Aurora — Aurora is a separate, more complex distributed engine and would be overkill for the simple 'managed PaaS Postgres' this toggle is meant to provide. Replaces the SQL Server Express container this deployment used before the app migrated from SQL Server to PostgreSQL — there is no SQL Server option anymore. The equivalent toggle in the Azure Terraform environment is named use_azure_postgresql (selecting Azure Database for PostgreSQL Flexible Server there) — deliberately NOT reused here, since this environment's managed path is Amazon RDS, not an Azure service."
  type        = bool
  default     = false
}

variable "postgres_password" {
  description = "Password for Segue's own Postgres database. In the containerized path (use_rds_postgresql = false) this is the 'segue' role's password, stored in Secrets Manager and injected into the container as POSTGRES_PASSWORD; in the managed path (true) this is the RDS instance's master password directly. Required either way — Terraform variables without a default must be provided."
  type        = string
  sensitive   = true
}

variable "rds_postgresql_instance_class" {
  description = "Amazon RDS for PostgreSQL instance class. db.t4g.micro (Graviton, burstable, currently the cheapest generally-available class) mirrors this file's existing keep-it-cheap-by-default convention (see the ECS task cpu/memory pairs in tasks.tf — e.g. redis at 256 cpu / 512 memory). Only consulted when use_rds_postgresql is true."
  type        = string
  default     = "db.t4g.micro"
}

variable "rds_postgresql_allocated_storage" {
  description = "Amazon RDS for PostgreSQL allocated storage, in GB. 20 is the platform minimum for gp2/gp3 storage on a Postgres instance. Storage can only be scaled up later, not down, so don't over-provision speculatively. Only consulted when use_rds_postgresql is true."
  type        = number
  default     = 20
}

variable "jwt_signing_key" {
  description = "FHIRBridge.Api's Authentication:SigningKey (HS256). At least 32 random characters."
  type        = string
  sensitive   = true
}

# --- Client-configurable ports ---
# segue_app_port only changes the ALB listener (what a client types after the ALB's DNS
# name) — the container itself keeps listening on its own fixed internal port (80, baked into the
# image), so changing this never requires a rebuild.
#
# postgres_port/redis_port are different: AWS Fargate's "awsvpc" networking has no host-level port
# remapping the way Docker Compose does, so the database/cache process itself must actually listen
# on the port declared here — tasks.tf passes each one through to the container (a `postgres -p`
# override for the containerized Postgres path, a `redis-server --port` override for Redis) rather
# than just relabeling a port mapping. Both are internal-only (reached via Cloud Map, never by an
# external client — see discovery.tf), so this only matters if you need them on non-default ports
# for your own network conventions. postgres_port is meaningless when use_rds_postgresql is
# true — Amazon RDS for PostgreSQL always uses 5432.

variable "segue_app_port" {
  description = "Public port clients use to reach segue-app through the load balancer."
  type        = number
  default     = 80
}

variable "postgres_port" {
  description = "Port the containerized Postgres listens on (internal-only, via Cloud Map). Passed to the container as a `postgres -p` override. Meaningless when use_rds_postgresql is true — Amazon RDS for PostgreSQL always uses 5432."
  type        = number
  default     = 5432
}

variable "redis_port" {
  description = "Port Redis listens on (internal-only, via Cloud Map). Passed to the container via a redis-server --port override."
  type        = number
  default     = 6379
}

variable "redis_password" {
  description = "Password Redis requires (--requirepass) — defense-in-depth on top of network isolation. Stored in Secrets Manager and injected as an env var at runtime, never written into the task definition in plaintext (see the redis task's command override in tasks.tf)."
  type        = string
  sensitive   = true
}

variable "redis_trusted_certificate_thumbprint" {
  description = "SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the segue-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn't match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs."
  type        = string
}

variable "enable_seq" {
  description = "false (default) — no Seq service; segue_app/worker log to CloudWatch only (via awslogs). true creates a Seq ECS service (datalust/seq, public image) reachable two ways: internally via Cloud Map (seq.<name_prefix>.internal, same as postgres/redis) so segue_app/worker can ship logs to it, and externally via a dedicated ALB listener on seq_port so a human can browse to it and monitor logs — protected by seq_admin_password, the only thing guarding that URL."
  type        = bool
  default     = false
}

variable "seq_admin_password" {
  description = "Admin password for the Seq web UI (SEQ_FIRSTRUN_ADMINPASSWORD) — required when enable_seq is true. Stored in Secrets Manager, never written into the task definition in plaintext. Ignored when enable_seq is false."
  type        = string
  sensitive   = true
  default     = ""
}

variable "seq_port" {
  description = "Public port clients use to reach Seq through the load balancer — deliberately a different port from segue_app_port, since each ALB listener is bound to exactly one port/target group. Only consulted when enable_seq is true."
  type        = number
  default     = 8443
}

