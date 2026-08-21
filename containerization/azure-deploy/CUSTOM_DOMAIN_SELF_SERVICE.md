# Segue / FHIRBridge — Custom domain + SSL self-service runbook

For clients and operators. Fixes the Marketplace/Deploy-to-Azure gap where a single-shot
domain+certificate deploy failed with `RequireCustomHostnameInEnvironment`.

## Why two phases?

Azure managed certificates require the **hostname to already exist** on a Container App in the
environment. Creating the cert first fails. Correct order:

1. Register hostname (`bindingType: Disabled`)
2. Create DNS (CNAME + `asuid` TXT)
3. Create managed cert + bind (`SniEnabled`)

## Path A — Deploy-to-Azure / Bicep wizard (preferred for clients)

### Phase 1 — Register hostname only

1. Open the Deploy-to-Azure link (or `az deployment group create` with `main.bicep`).
2. Enter custom domain(s) on the **Custom Domains** step.
3. Leave **Bind managed SSL certificates now** **UNCHECKED** (`bindCustomDomainCertificates=false`).
4. Deploy successfully.
5. From deployment outputs, copy:
   - `fhirbridgeAppUrl` / `demoAppUrl` (CNAME targets — use the hostname part)
   - `fhirbridgeAppDomainVerificationId` / `demoAppDomainVerificationId`
6. At your DNS provider create:
   - **CNAME** `your.domain` → `<app>.<env>.azurecontainerapps.io`
   - **TXT** `asuid.your.domain` → `<verification id>`
7. Wait for DNS propagation (minutes to hours). Check CAA if certs fail later.

### Phase 2 — Bind managed SSL

1. Redeploy with the **same** domain values.
2. **CHECK** **Bind managed SSL certificates now** (`bindCustomDomainCertificates=true`).
3. Deploy — Azure issues the free managed certificate and binds SNI.
4. Open `https://your.domain` and confirm the padlock.

### CLI example (Phase 1 then Phase 2)

```bash
# Phase 1
az deployment group create -g <rg> -n segue-phase1 -f main.bicep \
  -p namePrefix=segue6 sqlSaPassword=... jwtSigningKey=... redisPassword=... \
     fhirbridgeAppCustomDomain=app.example.com demoAppCustomDomain=demo.example.com \
     bindCustomDomainCertificates=false imageRegistryServer=fhirbridgevendor8ae7f3.azurecr.io \
     imageTag=v1.0.4 imageRegistryUsername=one-click-pull imageRegistryPassword=...

# After DNS is ready — Phase 2
az deployment group create -g <rg> -n segue-phase2 -f main.bicep \
  -p ...same as above... bindCustomDomainCertificates=true
```

## Path B — Operator script (any already-deployed app)

```powershell
cd containerization/scripts
.\manage-custom-domain.ps1 -ResourceGroup <rg> -AppName <prefix>-app `
  -EnvironmentName <prefix>-env -Domain app.example.com -Action Info
# create DNS, then:
.\manage-custom-domain.ps1 ... -Action Both -ValidationMethod CNAME
```

Bash: `manage-custom-domain.sh` with the same actions.

**IaC drift:** if you bind with the script, keep the matching domain (+ bind flag) in Bicep/TF
parameters on later applies or the next deploy may remove the hostname.

## Path C — Terraform (internal)

Same two-phase flags in `terraform/environments/azure`:

- `fhirbridge_app_custom_domain` / `demo_app_custom_domain`
- `bind_custom_domain_certificates = false` then `true` after DNS

## Marketplace / Partner Center (org process — not code)

After this domain flow is validated live:

1. Partner Center publisher account
2. CreateUiDefinition sandbox validation
3. Confirm vendor ACR access model (token-based is what production uses today)
4. Preview/test tenant dry-run
5. Microsoft certification submit

## Do not

- Check “Bind SSL” on the first domain deploy for a brand-new hostname
- Test in subscriptions other than the agreed Ragu / Sponsorship test directory
- Leave custom domains out of template params after binding them with the script
