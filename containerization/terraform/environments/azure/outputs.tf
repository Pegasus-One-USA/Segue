output "acr_login_server" {
  description = "Push the 3 custom images here before the first apply (or before updating image_tag): containerization/scripts/build-images.sh -r <this> -t <image_tag> -p"
  value       = azurerm_container_registry.acr.login_server
}

output "fhirbridge_app_url" {
  value = "https://${local.fhirbridge_app_name}.${azurerm_container_app_environment.main.default_domain}"
}

output "demo_app_url" {
  value = "https://${local.demo_app_name}.${azurerm_container_app_environment.main.default_domain}"
}
