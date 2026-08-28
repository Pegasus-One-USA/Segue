# Persistent Container Registry for FHIRBridge release images — lives in the vendor's own Azure
# tenant/subscription, entirely separate from any client deployment. Exists so a version, once
# published, has one durable home versioned with real, manually-assigned semantic-version tags —
# rather than being rebuilt (and re-pushed to a client's registry) every time someone deploys.
#
# Deliberately built with LOCAL Docker (build-images.ps1|sh -Registry <this acr's login server>
# -Tag vX.Y.Z -Push), not `az acr build`/ACR Tasks: building remotely means uploading the full
# source tree to a build sandbox, even one scoped to this same subscription — building locally
# means source never leaves the machine that already has it; only the compiled image gets pushed.
# Slower to publish a new version this way (Docker has to be running wherever you run the build
# script), but that's an infrequent, deliberate release action — not something a client (or anyone
# installing this product) ever needs to do.
#
# A client deployment (../environments/azure, ../environments/aws) never builds against or pulls
# directly from this registry on its own — that would mean the client's Azure account needing
# credentials for it. The intended flow is: publish a version here (compiled image only, from a
# local build), then `az acr import` that specific version into whichever registry the client
# deployment actually creates for itself — a server-to-server image copy, no source involved
# either way. This config only creates the registry itself, not that distribution step.
#
# Deliberately its own environment, NOT part of ../environments/azure: that environment gets destroyed and
# recreated repeatedly during testing (cleanup-azure.ps1/sh), and this registry needs to persist
# across all of that — it holds actual release history, not disposable test infrastructure.

terraform {
  required_version = ">= 1.6.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {}

  # Same reasoning as ../environments/azure/main.tf: avoids requiring subscription-level permission to
  # auto-register resource providers this account doesn't need and may not be able to register.
  skip_provider_registration = true
}

resource "random_id" "suffix" {
  byte_length = 3
}

# Deploys into an EXISTING resource group — this config never creates or destroys it. Currently
# points at rg-tusharpuri (var.resource_group_name's default), the only resource group this
# account has access to right now — same group ../environments/azure deploys into. Purely a
# variable, so switching to a dedicated resource group later (recommended, since this registry is
# meant to outlive any number of test teardowns there) is a one-line terraform.tfvars change, no
# code edits needed.
data "azurerm_resource_group" "main" {
  name = var.resource_group_name
}

locals {
  # alnum only, globally unique. Prefer var.acr_name_override (a real, memorable name) once one has
  # been chosen — this auto-generated pattern only exists as a collision-free fallback.
  acr_name = var.acr_name_override != "" ? var.acr_name_override : "${var.name_prefix}vendor${random_id.suffix.hex}"

  common_tags = {
    Project     = "FHIRBridge"
    Component   = "vendor-registry"
    Environment = var.name_prefix
    ManagedBy   = "Terraform"
  }
}

resource "azurerm_container_registry" "vendor" {
  name                = local.acr_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = data.azurerm_resource_group.main.location
  sku                 = "Basic"

  # --- Access protection: nobody gets in without an explicit, revocable grant ---
  #
  # Admin credentials stay off — `az acr login`/`az acr import` both authenticate via your own
  # Azure AD identity (whatever role you already have on this resource group), not a shared
  # admin user/password that could leak and can't be individually revoked.
  admin_enabled = false
  # Explicitly disabled (this is also the platform default, but spelled out here so the intent is
  # visible in code, not just assumed): nobody can pull an image without first authenticating as
  # somebody with an actual role grant on this registry. No public/unauthenticated pulls, ever.
  anonymous_pull_enabled = false
  tags                   = local.common_tags

  # When a real external client eventually needs pull access (the "client uses their own Azure
  # account" phase mentioned in the containerization docs), the safe mechanism is an ACR "token" —
  # a repository-scoped, read-only, individually revocable credential
  # (az acr token create --registry <this> --name <client-name> --scope-map _repositories_pull) —
  # not admin credentials and not a broad role grant. Not created here since no external client
  # exists yet; add one per client when that's actually needed.
}
