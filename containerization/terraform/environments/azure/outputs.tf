output "acr_login_server" {
  description = "Push the 3 custom images here before the first apply (or before updating image_tag): containerization/scripts/build-images.sh -r <this> -t <image_tag> -p. hapi-terminology is 4th and different -- it's a stock third-party image, not built from our own Dockerfile, so it's imported instead: az acr import --name <this ACR's name, not the full login server> --source docker.io/hapiproject/hapi:latest --image hapi-terminology:<image_tag>"
  value       = azurerm_container_registry.acr.login_server
}

output "key_vault_name" {
  description = "Secrets source of truth. Rotate a secret with: az keyvault secret set --vault-name <this> --name <sql-sa-password|jwt-signing-key|redis-password> --value <new-value>, then restart the affected Container App revisions to pick it up."
  value       = azurerm_key_vault.main.name
}

output "tenant_secrets_key_vault_id" {
  description = "Populated only when enable_tenant_secrets_key_vault is true. Scope to use in a manual 'az role assignment create --role \"Key Vault Secrets Officer\" --assignee <principal_id> --scope <this>' if the azurerm_role_assignment resources in main.tf fail for lack of Owner/User Access Administrator rights."
  value       = var.enable_tenant_secrets_key_vault ? data.azurerm_key_vault.tenant_secrets[0].id : null
}

output "fhirbridge_app_principal_id" {
  description = "System-assigned managed identity principal ID for the fhirbridge_app Container App. Use as --assignee for the manual role-assignment fallback above."
  value       = azurerm_container_app.fhirbridge_app.identity[0].principal_id
}

output "worker_principal_id" {
  description = "System-assigned managed identity principal ID for the worker Container App. Use as --assignee for the manual role-assignment fallback above."
  value       = azurerm_container_app.worker.identity[0].principal_id
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

output "hapi_terminology_url" {
  description = "Only reachable once hapi_terminology_external_access is true (default false — internal-only otherwise), or a custom domain is set, which forces it on."
  value       = "https://${local.hapi_terminology_name}.${azurerm_container_app_environment.main.default_domain}"
}

output "fhirbridge_app_domain_verification" {
  description = "Add CNAME (domain -> fhirbridge_app_url hostname) and TXT asuid.<domain>=this value at your DNS provider to register the hostname. See fhirbridge_app_custom_domain's description for the current SSL-binding limitation (bind_custom_domain_certificates is a no-op until this environment is migrated to azurerm ~> 4.69)."
  value       = azurerm_container_app.fhirbridge_app.custom_domain_verification_id
}

output "demo_app_domain_verification" {
  description = "Same idea as fhirbridge_app_domain_verification, for the demo app's custom domain."
  value       = azurerm_container_app.demo_app.custom_domain_verification_id
}

output "hapi_terminology_domain_verification" {
  description = "Same idea as fhirbridge_app_domain_verification, for the terminology server's custom domain. Always available regardless of hapi_terminology_external_access — needed even before external ingress is on, to prep DNS ahead of time."
  value       = azurerm_container_app.hapi_terminology.custom_domain_verification_id
}

output "fhirbridge_app_custom_domain_url" {
  description = "Populated once fhirbridge_app_custom_domain is set; null otherwise."
  value       = var.fhirbridge_app_custom_domain != "" ? "https://${var.fhirbridge_app_custom_domain}" : null
}

output "demo_app_custom_domain_url" {
  description = "Populated once demo_app_custom_domain is set; null otherwise."
  value       = var.demo_app_custom_domain != "" ? "https://${var.demo_app_custom_domain}" : null
}

output "hapi_terminology_custom_domain_url" {
  description = "Populated once hapi_terminology_custom_domain is set; null otherwise."
  value       = var.hapi_terminology_custom_domain != "" ? "https://${var.hapi_terminology_custom_domain}" : null
}

# Currently always false in effect — see bind_custom_domain_certificates' own description
# (azurerm ~> 3.100 lacks the managed-certificate resource this flag would drive).
output "bind_custom_domain_certificates" {
  value = var.bind_custom_domain_certificates
}

output "custom_domain_phase" {
  value = (var.fhirbridge_app_custom_domain == "" && var.demo_app_custom_domain == "" && var.hapi_terminology_custom_domain == "") ? "none" : "hostname-registered-no-managed-ssl-until-azurerm-4.69-migration"
}
