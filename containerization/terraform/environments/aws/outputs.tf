output "ecr_repository_urls" {
  description = "Push the 3 custom images here before the first full apply: containerization/scripts/build-images.sh -r <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -t <image_tag> -p"
  value = {
    segue_app = aws_ecr_repository.segue_app.repository_url
    worker         = aws_ecr_repository.worker.repository_url
    redis          = aws_ecr_repository.redis.repository_url
  }
}

output "segue_app_url" {
  description = "Uses a self-signed certificate — expect a browser trust warning until this is swapped for a real ACM certificate (see main.tf)."
  value       = "https://${aws_lb.main.dns_name}:${var.segue_app_port}"
}

output "seq_url" {
  description = "Populated only when enable_seq is true. Log into this with the seq_admin_password you set to browse structured logs from Api/Gateway/Worker. Same self-signed-certificate caveat as segue_app_url."
  value       = var.enable_seq ? "https://${aws_lb.main.dns_name}:${var.seq_port}" : null
}

output "postgres_mode" {
  description = "Which Postgres Segue's own database actually has — \"rds-managed\" or \"container\"."
  value       = var.use_rds_postgresql ? "rds-managed" : "container"
}

output "rds_postgresql_endpoint" {
  description = "Populated only when use_rds_postgresql is true. Same host ConnectionStrings:FHIRBridgeDb points the app at — useful for connecting a client (e.g. psql/pgAdmin) directly for debugging."
  value       = var.use_rds_postgresql ? aws_db_instance.postgresql[0].address : null
}
