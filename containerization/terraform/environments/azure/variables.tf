variable "name_prefix" {
  description = "Short name used to build resource names (resource group, ACR, storage account, Container Apps)."
  type        = string
  default     = "fhirbridge"
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "image_tag" {
  description = "Tag the 3 custom images were pushed to ACR with (containerization/scripts/build-images.sh|ps1 -Registry <this ACR's login server> -Tag <this> -Push)."
  type        = string
  default     = "latest"
}

variable "sql_sa_password" {
  description = "SQL Server SA password. Must satisfy SQL Server's complexity policy."
  type        = string
  sensitive   = true
}

variable "jwt_signing_key" {
  description = "FHIRBridge.Api's Authentication:SigningKey (HS256). At least 32 random characters."
  type        = string
  sensitive   = true
}
