variable "name_prefix" {
  description = "Short name used to build the registry name, UNLESS acr_name_override is set (see that variable)."
  type        = string
  default     = "segue"
}

variable "acr_name_override" {
  description = "Explicit registry name, bypassing the auto-generated \"$${name_prefix}vendor$${random suffix}\" pattern entirely. Set once you've picked a real, memorable name (ACR names are globally unique across all of Azure — check availability first with `az acr check-name --name <candidate>`) rather than living with an auto-generated one like the original seguevendor8ae7f3. ACR names cannot be renamed after creation, so changing this on an EXISTING deployment means Terraform will try to create a new registry rather than rename the old one — either `terraform import azurerm_container_registry.vendor <existing resource id>` first if you manually created/renamed it outside Terraform (as happened when this registry was renamed to seguebuilds), or accept that a real create+retire-old cycle is required."
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Name of the EXISTING resource group this persistent registry is created into. Never created or destroyed by this config — create it once yourself first if it doesn't already exist: az group create --name <this> --location <region>. Currently defaults to rg-tusharpuri (the only resource group this account has access to) — ideally this would be a DIFFERENT, dedicated resource group from a per-deployment test environment (e.g. ../environments/azure also uses rg-tusharpuri right now), since this registry is meant to persist independently of test deployments being torn down and recreated. Switch to a dedicated group once one is available."
  type        = string
  default     = "rg-tusharpuri"
}
