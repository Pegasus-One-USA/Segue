# Vendor Registry Cleanup & Rename — Command Log

Every `az`/shell command run against the live Azure subscription during this session's vendor
registry work, in order, with the reason for each. Kept as an audit trail since these are
real, mostly-irreversible operations against `rg-tusharpuri`.

## 1. Clearing old versions from `fhirbridgevendor8ae7f3` (explicitly confirmed by user)

```bash
az acr repository list --name fhirbridgevendor8ae7f3 -o table
```
**Why:** See what repositories existed before deciding whether/what to clear.

```bash
az acr repository show-tags --name fhirbridgevendor8ae7f3 --repository demo-app -o table
az acr repository show-tags --name fhirbridgevendor8ae7f3 --repository fhirbridge-app -o table
az acr repository show-tags --name fhirbridgevendor8ae7f3 --repository fhirbridge-worker -o table
```
**Why:** Confirm exactly which tagged versions existed (found `v1.0.0`–`v1.0.7.1`, 10 tags each)
before asking the user to confirm the destructive scope.

```bash
az acr repository delete --name fhirbridgevendor8ae7f3 --repository demo-app --yes
az acr repository delete --name fhirbridgevendor8ae7f3 --repository fhirbridge-app --yes
az acr repository delete --name fhirbridgevendor8ae7f3 --repository fhirbridge-worker --yes
```
**Why:** User explicitly confirmed (after being warned this deletes persistent release history)
they wanted all existing versions cleared before pushing a fresh build from the current branch.
Deletes all tags + manifests for each repository. **Note:** user later clarified that going
forward, only individual tags should be pruned, never a full repository delete — see
`feedback_acr_no_full_repo_delete` in the auto-memory system. This one full-repo deletion was
explicitly authorized before that guidance was given.

```bash
az acr repository list --name fhirbridgevendor8ae7f3 -o table
```
**Why:** Confirm the registry was actually empty afterward (returned nothing).

## 2. Renaming the vendor registry (ACR names can't be renamed in place — this is create-new + retire-old)

```bash
az acr check-name --name fhirbridgevendor
az acr check-name --name pegasusonefhirbridge
az acr check-name --name fhirbridgereleases
az acr check-name --name pegasusfhirbridge
az acr check-name --name fhirbridgeregistry
```
**Why:** Before proposing name options to the user, confirm each candidate is actually available
(ACR names are globally unique across all of Azure).

```bash
az acr check-name --name seguebuilds
```
**Why:** User chose `seguebuilds` from the offered options; confirmed it's available before
creating anything.

```bash
az acr create --name seguebuilds --resource-group rg-tusharpuri --sku Basic --admin-enabled false \
  --location eastus --tags Project=FHIRBridge Component=vendor-registry Environment=fhirbridge ManagedBy=Terraform
```
**Why:** Create the new, friendlier-named registry. Matches the exact security posture the
Terraform module (`containerization/terraform/vendor-registry/main.tf`) defines for this resource:
`admin_enabled = false` (no shared admin credential — access is via individual Azure AD identity
or scoped tokens only) and Basic SKU (matches the original).

```bash
az acr token create --registry seguebuilds --name one-click-pull --scope-map _repositories_pull
```
**Why:** Recreate the same read-only, repository-pull-only, individually-revocable token the old
registry had (`one-click-pull`) — this is what `containerization/azure-deploy/createUiDefinition.json`
embeds so the "Deploy to Azure" wizard can pull images without exposing any broader credential.
Generates a fresh password since the old registry (and its token) is being retired.

```bash
az acr delete --name fhirbridgevendor8ae7f3 --yes
```
**Why:** Retire the old registry now that its replacement exists and there's nothing left in it to
migrate (already emptied in step 1). **Status: BLOCKED by the auto-mode classifier** (destructive
action) — needs to be run by the user directly, not executed by Claude. Left for the user to run.

## Files updated to reference the new registry (`seguebuilds.azurecr.io`) instead of the old one

- `containerization/azure-deploy/createUiDefinition.json` — `imageRegistryServer` and
  `imageRegistryPassword` (the new token's password) updated.
- `containerization/terraform/vendor-registry/variables.tf` — added a new `acr_name_override`
  variable so the ACR's name can be pinned to a real, memorable string instead of always falling
  back to the auto-generated `<name_prefix>vendor<random suffix>` pattern. Documents the
  create-new/retire-old reality (ACR names can't be renamed in place) and the `terraform import`
  path for reconciling a manually-created/renamed registry like this one.
- `containerization/terraform/vendor-registry/main.tf` — `local.acr_name` now prefers
  `var.acr_name_override` when set, falling back to the old auto-generated pattern otherwise.
- `containerization/terraform/vendor-registry/terraform.tfvars.example` — added
  `acr_name_override = "seguebuilds"` (the real name currently in use) with a comment explaining
  when to remove it.
- `deploy-vendor-v1.1.0.ps1` (repo root) — registry name/login server updated to `seguebuilds`.
- `containerization/azure-deploy/CUSTOM_DOMAIN_SELF_SERVICE.md` — one illustrative
  `imageRegistryServer=` example line updated.
- `Documents/Containerization-Setup-Steps.html` (9 occurrences) and
  `Documents/Containerization-OneClick-Files-And-Commands.html` (5 occurrences) — mechanical
  find/replace of every `fhirbridgevendor8ae7f3` reference to `seguebuilds` (all were illustrative
  usages of this exact resource name in walkthrough text/commands, confirmed via `grep -c` before
  and after the `sed` replace to verify the count matched and nothing else changed).

## Verification performed

- `terraform fmt -check` and `terraform validate` (against a scratch copy of
  `containerization/terraform/vendor-registry`, since real credentials/state aren't available in
  the validation sandbox) both passed clean after the `acr_name_override` addition.
- `grep -c` before/after the HTML `sed` replace confirmed exactly 14 occurrences (9 + 5) were
  changed and zero of the old name remained.

## Still outstanding

- **`az acr delete --name fhirbridgevendor8ae7f3 --yes` — user needs to run this themselves**
  (blocked by the auto-mode classifier as a destructive action). The new registry (`seguebuilds`)
  is fully set up and ready; the old one is empty but not yet deleted.
- `deploy-vendor-v1.1.0.ps1` still has 4 `CHANGE-ME-*` placeholder secrets that need real values
  filled in before running (unrelated to the rename — a pre-existing note from when the script was
  first written).
