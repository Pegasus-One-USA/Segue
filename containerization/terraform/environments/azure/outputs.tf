output "acr_login_server" {
  description = "Push the 3 custom images here before the first apply (or before updating image_tag): containerization/scripts/build-images.sh -r <this> -t <image_tag> -p"
  value       = azurerm_container_registry.acr.login_server
}

output "key_vault_name" {
  description = "Secrets source of truth. Rotate a secret with: az keyvault secret set --vault-name <this> --name <postgres-password|jwt-signing-key|redis-password> --value <new-value>, then restart the affected Container App revisions to pick it up."
  value       = azurerm_key_vault.main.name
}

output "tenant_secrets_key_vault_id" {
  description = "Populated only when enable_tenant_secrets_key_vault is true. Scope to use in a manual 'az role assignment create --role \"Key Vault Secrets Officer\" --assignee <principal_id> --scope <this>' if the azurerm_role_assignment resources in main.tf fail for lack of Owner/User Access Administrator rights."
  value       = var.enable_tenant_secrets_key_vault ? azurerm_key_vault.tenant_secrets[0].id : null
}

output "redis_mode" {
  description = "Which Redis this deployment actually has — \"azure-cache\" or \"container\"."
  value       = var.use_azure_cache_for_redis ? "azure-cache" : "container"
}

output "azure_cache_hostname" {
  description = "Populated only when use_azure_cache_for_redis is true. Same host ConnectionStrings:Redis points the app at — useful for connecting a client (e.g. redis-cli / RedisInsight) directly for debugging."
  value       = var.use_azure_cache_for_redis ? azurerm_redis_cache.main[0].hostname : null
}

output "azure_cache_primary_access_key" {
  description = "Populated only when use_azure_cache_for_redis is true. Rotate with 'az redis regenerate-keys' (or the Portal) — this output will reflect the new value on the next apply/refresh."
  value       = var.use_azure_cache_for_redis ? azurerm_redis_cache.main[0].primary_access_key : null
  sensitive   = true
}

output "postgres_mode" {
  description = "Which Postgres FHIRBridge's own database actually has — \"azure-managed\" or \"container\"."
  value       = var.use_azure_postgresql ? "azure-managed" : "container"
}

output "azure_postgresql_fqdn" {
  description = "Populated only when use_azure_postgresql is true. Same host ConnectionStrings:FHIRBridgeDb points the app at — useful for connecting a client (e.g. psql / pgAdmin) directly for debugging."
  value       = var.use_azure_postgresql ? azurerm_postgresql_flexible_server.main[0].fqdn : null
}

output "tenant_secrets_key_vault_name" {
  description = "Populated only when enable_tenant_secrets_key_vault is true. Name of the Key Vault this config created for tenant/app-level secrets — use with 'az keyvault secret list --vault-name <this>' to see what the app has provisioned there so far."
  value       = var.enable_tenant_secrets_key_vault ? azurerm_key_vault.tenant_secrets[0].name : null
}

output "tenant_secrets_key_vault_uri" {
  description = "Populated only when enable_tenant_secrets_key_vault is true. Same value the app receives as KeyVault:VaultName."
  value       = var.enable_tenant_secrets_key_vault ? azurerm_key_vault.tenant_secrets[0].vault_uri : null
}

output "dataprotection_key_id" {
  description = "Populated only when enable_tenant_secrets_key_vault is true. Same value the app receives as DataProtection:KeyVaultKeyId."
  value       = var.enable_tenant_secrets_key_vault ? azurerm_key_vault_key.dataprotection[0].id : null
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

output "seq_url" {
  description = "Populated only when enable_seq is true. Log into this with the seq_admin_password you set to browse structured logs from Api/Gateway/Worker."
  value       = var.enable_seq ? "https://${local.seq_name}.${azurerm_container_app_environment.main.default_domain}" : null
}

output "fhirbridge_app_domain_verification" {
  description = "Add CNAME (domain -> fhirbridge_app_url hostname) and TXT asuid.<domain>=this value at your DNS provider to register the hostname. See fhirbridge_app_custom_domain's description for the current SSL-binding limitation (bind_custom_domain_certificates is a no-op until this environment is migrated to azurerm ~> 4.69)."
  value       = azurerm_container_app.fhirbridge_app.custom_domain_verification_id
}

output "fhirbridge_app_custom_domain_url" {
  description = "Populated once fhirbridge_app_custom_domain is set; null otherwise."
  value       = var.fhirbridge_app_custom_domain != "" ? "https://${var.fhirbridge_app_custom_domain}" : null
}

# Currently always false in effect — see bind_custom_domain_certificates' own description
# (azurerm ~> 3.100 lacks the managed-certificate resource this flag would drive).
output "bind_custom_domain_certificates" {
  value = var.bind_custom_domain_certificates
}

output "custom_domain_phase" {
  value = var.fhirbridge_app_custom_domain == "" ? "none" : "hostname-registered-no-managed-ssl-until-azurerm-4.69-migration"
}
