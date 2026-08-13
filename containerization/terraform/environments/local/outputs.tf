output "fhirbridge_app_url" {
  value = "http://localhost:${var.app_host_port}"
}

output "demo_app_url" {
  value = "http://localhost:${var.demo_host_port}"
}
