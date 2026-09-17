output "acr_name" {
  description = "Short registry name. Use with: az acr login --name <this> (before a local docker push)."
  value       = azurerm_container_registry.vendor.name
}

output "acr_login_server" {
  description = "Full registry hostname. Use as: (1) the -Registry value for build-images.ps1|sh when publishing a new version from a local build, and (2) the --source prefix when importing a published version into a client deployment's own registry, e.g.: az acr import --name <client-acr> --source <this>/segue-app:v1.2.0 --image segue-app:v1.2.0"
  value       = azurerm_container_registry.vendor.login_server
}
