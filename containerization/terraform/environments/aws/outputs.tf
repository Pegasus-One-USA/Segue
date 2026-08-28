output "ecr_repository_urls" {
  description = "Push the 5 custom/mirrored images here before the first full apply: containerization/scripts/build-images.sh -r <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -t <image_tag> -p"
  value = {
    fhirbridge_app   = aws_ecr_repository.fhirbridge_app.repository_url
    demo_app         = aws_ecr_repository.demo_app.repository_url
    worker           = aws_ecr_repository.worker.repository_url
    redis            = aws_ecr_repository.redis.repository_url
    hapi_terminology = aws_ecr_repository.hapi_terminology.repository_url
  }
}

output "fhirbridge_app_url" {
  description = "Uses a self-signed certificate — expect a browser trust warning until this is swapped for a real ACM certificate (see main.tf)."
  value       = "https://${aws_lb.main.dns_name}:${var.fhirbridge_app_port}"
}

output "demo_app_url" {
  description = "Uses a self-signed certificate — expect a browser trust warning until this is swapped for a real ACM certificate (see main.tf)."
  value       = "https://${aws_lb.main.dns_name}:${var.demo_app_port}"
}

output "hapi_terminology_url" {
  description = "Only reachable when hapi_terminology_external_access = true (default false — internal-only otherwise). Uses a self-signed certificate — expect a browser trust warning until this is swapped for a real ACM certificate (see main.tf). Point your own DNS (CNAME) at aws_lb.main.dns_name for a custom domain."
  value       = var.hapi_terminology_external_access ? "https://${aws_lb.main.dns_name}:${var.hapi_terminology_port}" : null
}
