output "ecr_repository_urls" {
  description = "Push the 3 custom images here before the first full apply: containerization/scripts/build-images.sh -r <account>.dkr.ecr.<region>.amazonaws.com/<name_prefix> -t <image_tag> -p"
  value = {
    fhirbridge_app = aws_ecr_repository.fhirbridge_app.repository_url
    demo_app       = aws_ecr_repository.demo_app.repository_url
    worker         = aws_ecr_repository.worker.repository_url
  }
}

output "fhirbridge_app_url" {
  value = "http://${aws_lb.main.dns_name}"
}

output "demo_app_url" {
  value = "http://${aws_lb.main.dns_name}:5500"
}
