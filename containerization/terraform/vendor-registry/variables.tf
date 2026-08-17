variable "name_prefix" {
  description = "Short name used to build the registry name."
  type        = string
  default     = "segue"
}

variable "resource_group_name" {
  description = "Name of the EXISTING resource group this persistent registry is created into. Never created or destroyed by this config — create it once yourself first if it doesn't already exist: az group create --name <this> --location <region>. Currently defaults to rg-tusharpuri (the only resource group this account has access to) — ideally this would be a DIFFERENT, dedicated resource group from a per-deployment test environment (e.g. ../environments/azure also uses rg-tusharpuri right now), since this registry is meant to persist independently of test deployments being torn down and recreated. Switch to a dedicated group once one is available."
  type        = string
  default     = "rg-tusharpuri"
}
