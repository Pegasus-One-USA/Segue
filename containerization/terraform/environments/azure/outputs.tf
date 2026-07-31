output "acr_login_server" {
  description = "Push the 3 custom images here before the first apply (or before updating image_tag): containerization/scripts/build-images.sh -r <this> -t <image_tag> -p"
  value       = azurerm_container_registry.acr.login_server
}

output "key_vault_name" {
  description = "Secrets source of truth. Rotate a secret with: az keyvault secret set --vault-name <this> --name <sql-sa-password|jwt-signing-key|redis-password> --value <new-value>, then restart the affected Container App revisions to pick it up."
  value       = azurerm_key_vault.main.name
}

output "resource_manifest_download_cmd" {
  description = "Every resource ID this config created, as a fallback for cleanup from a machine without this environment's terraform.tfstate. Download with this command, then see the file's own header for how to delete each listed resource directly."
  value       = "az storage blob download --account-name ${azurerm_storage_account.main.name} --container-name ${azurerm_storage_container.manifest.name} --name ${azurerm_storage_blob.resource_manifest.name} --file resources.txt --auth-mode login"
}

output "fhirbridge_app_url" {
  value = "https://${local.fhirbridge_app_name}.${azurerm_container_app_environment.main.default_domain}"
}

output "demo_app_url" {
  value = "https://${local.demo_app_name}.${azurerm_container_app_environment.main.default_domain}"
}

output "fhirbridge_app_domain_verification" {
  description = "Always available, regardless of whether fhirbridge_app_custom_domain is set. Add a CNAME (your domain -> fhirbridge_app_url's hostname) and a TXT record named asuid.<your domain> with this value at your DNS provider, wait for propagation, THEN set fhirbridge_app_custom_domain and re-apply."
  value       = azurerm_container_app.fhirbridge_app.custom_domain_verification_id
}

output "demo_app_domain_verification" {
  description = "Same idea as fhirbridge_app_domain_verification, for the demo app's custom domain."
  value       = azurerm_container_app.demo_app.custom_domain_verification_id
}

output "fhirbridge_app_custom_domain_url" {
  description = "Populated once fhirbridge_app_custom_domain is set and that apply has completed; null otherwise."
  value       = var.fhirbridge_app_custom_domain != "" ? "https://${var.fhirbridge_app_custom_domain}" : null
}

output "demo_app_custom_domain_url" {
  description = "Populated once demo_app_custom_domain is set and that apply has completed; null otherwise."
  value       = var.demo_app_custom_domain != "" ? "https://${var.demo_app_custom_domain}" : null
}
