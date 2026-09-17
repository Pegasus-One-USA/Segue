// Segue — single-deployment Azure Container Apps template.
//
// This is the "easy install" counterpart to ../terraform/environments/azure: same 4-container
// topology (segue-app, postgres, redis, worker), same Container Apps Environment
// design, but expressed as one Bicep template so it can be deployed with a single command or a
// single "Deploy to Azure" button click, instead of a multi-step `terraform apply`.
//
// Segue's own database (Postgres) is either containerized (default) or a managed Azure
// Database for PostgreSQL Flexible Server — see the useAzurePostgresql parameter below. Replaces
// the SQL Server Express container this deployment used before the app migrated from SQL Server to
// PostgreSQL — there is no SQL Server option anymore. The containerized path runs Postgres on LOCAL
// (ephemeral) disk, not directly on Azure Files — Postgres's own startup permission check can never
// pass on an Azure Files/SMB mount (confirmed: chown/chmod always fails there, and Container Apps'
// azureFile storage type has no mount-options/NFS escape hatch), so a custom image
// (containerization/docker/postgres-local) instead treats Azure Files purely as an at-rest backup
// target, restored into local storage on start and saved back out on a graceful stop only — see
// that image's Dockerfile/entrypoint.sh for the real durability tradeoff this implies.
//
// Resource-group scoped: the customer picks/creates the resource group in the Azure Portal's
// deployment wizard (or via -g on the CLI) — this template does not create the resource group
// itself.
//
// IMPORTANT — image publishing is a prerequisite, not something this template does: a genuinely
// one-click deploy requires the 4 custom images (segue-app, segue-worker,
// segue-redis, segue-postgres) to already exist in a registry the customer's Container
// Apps can reach BEFORE they click deploy. segue-redis is stock redis:7-alpine plus a
// self-signed TLS certificate generated locally (containerization/docker/redis-tls/generate-cert.
// ps1|sh — run once before building images) — see that Dockerfile for why Redis needs a custom
// image at all, and this template's redisTrustedCertificateThumbprint parameter for wiring the
// printed thumbprint in. segue-postgres is stock postgres:16-alpine plus the backup/restore
// entrypoint described above — see containerization/docker/postgres-local/Dockerfile. Build and
// push images once (see containerization/scripts/build-images.sh|ps1) to whatever registry you
// control, then point imageRegistryServer/imageTag at that release. See README.md in this folder
// for the full publishing + one-click deploy story.

@description('Short name used to build every resource name in this deployment.')
param namePrefix string = 'segue'

@description('Azure region for every resource. Defaults to the resource group\'s own region.')
param location string = resourceGroup().location

@description('Chooses which Postgres Segue\'s own database (FHIRBridgeDb) gets. true (default, recommended) creates a managed Azure Database for PostgreSQL Flexible Server and points ConnectionStrings:FHIRBridgeDb at it over a required SSL connection — no container, no volume; Azure manages patching, and (the reason this became the default) automated daily backups with point-in-time restore for a configurable window. false instead keeps a containerized Postgres (stock postgres:16-alpine, single replica) — its data lives on the container\'s own local disk and is only copied out to Azure Files on a graceful shutdown (see postgresApp\'s own comment), so a crash can lose recent data with no fixed limit. Choosing false also creates postgresBackupJob below — a scheduled pg_dump-to-Blob-Storage job that meaningfully reduces that risk (bounds data loss to one backup interval instead of "unpredictable"), but it is a compensating control, not parity with Flexible Server\'s point-in-time restore: restoring means manually running psql against the newest dump, not a one-click Azure restore.')
param useAzurePostgresql bool = true

@description('Password for Segue\'s own Postgres database. In the containerized path (useAzurePostgresql = false) this is the \'segue\' role\'s password; in the managed path (true) this is the Flexible Server\'s administrator password directly. Required either way.')
@secure()
param postgresPassword string

@description('FHIRBridge.Api\'s Authentication:SigningKey (HS256). At least 32 random characters.')
@secure()
@minLength(32)
param jwtSigningKey string

@description('Registry every custom image this deployment references was published to (e.g. myregistry.azurecr.io, or ghcr.io/your-org for a public GHCR package) — segue-app/-worker/-redis/-postgres always, plus segue-postgres-backup too if useAzurePostgresql is false.')
param imageRegistryServer string

@description('Tag every custom image this deployment references was published under. Pinned to a released version on purpose — NOT \'latest\'. A customer deploying this template months from now must get exactly the build that was tested and published as that version, not whatever happens to be sitting in the registry that day; \'latest\' also makes "which version is broken?" unanswerable on a support call. The release pipeline (.github/workflows/release.yml) rewrites this default to the version being released, so the published template always names its own build.')
param imageTag string = '1.0.0'

@description('Registry username. Leave blank if the registry allows anonymous/public pull (e.g. a public GHCR package) — no registry credentials are configured in that case.')
@secure()
param imageRegistryUsername string = ''

@description('Registry password/token. Leave blank alongside imageRegistryUsername for anonymous/public pull.')
@secure()
param imageRegistryPassword string = ''

@description('Port the containerized Postgres listens on (internal-only — reached only by the other Container Apps in this environment, never externally). Passed to the container via a `-p` args override (through the image\'s own entrypoint, not replacing it), since Postgres has no env-var port setting. Meaningless when useAzurePostgresql is true — Azure Database for PostgreSQL always uses 5432.')
param postgresPort int = 5432

@description('Port Redis listens on (internal-only). Passed to the container via a redis-server --port override, since Redis has no env-var port setting.')
param redisPort int = 6379

@description('Chooses which Redis this deployment gets. false (default) keeps the existing containerized Redis — self-signed TLS cert baked into the segue-redis image, Azure Files-backed persistence, single replica, requires redisPassword/redisTrustedCertificateThumbprint. true creates an Azure Managed Redis cluster instead (Microsoft.Cache/redisEnterprise — classic Microsoft.Cache/redis is being retired and is already blocked for new caches in some subscriptions, see https://aka.ms/AzureCacheForRedisRetirement) and points ConnectionStrings:Redis at it — no container, no volume, no self-signed cert to generate; Azure issues its own CA-trusted certificate, which FHIRBridge.Api/.Worker accept automatically. redisPassword and redisTrustedCertificateThumbprint are both ignored in this mode — Azure Managed Redis manages its own access keys and presents its own trusted certificate.')
param useAzureCacheForRedis bool = false

@description('Password the containerized Redis requires (--requirepass) — defense-in-depth on top of network isolation. Passed as a plain container command argument (Redis has no env-var equivalent and Container Apps command arguments have no secretRef option), so it is visible to anyone with read access to this Container App\'s configuration — same exposure level as any other command argument. Required when useAzureCacheForRedis is false; ignored when true (Azure Cache manages its own access keys).')
@secure()
param redisPassword string = ''

@description('SHA-1 thumbprint (X509Certificate2.Thumbprint format, e.g. 8638036B0BE54FADF44EEDBFCD2CEC1A80BBB37F) of the self-signed certificate baked into the segue-redis image you built — run containerization/docker/redis-tls/generate-cert.ps1|sh once before building images, which prints this value. FHIRBridge.Api/.Worker refuse the Redis connection if this doesn\'t match what Redis actually presents (fails closed, not open) — see ValidateRedisServerCertificate in src/FHIRBridge.Infrastructure/DependencyInjection.cs. Required when useAzureCacheForRedis is false; leave blank when true — Azure Cache presents a normal CA-trusted certificate that needs no pinning.')
param redisTrustedCertificateThumbprint string = ''

@description('Azure Managed Redis SKU — a curated subset of the full Microsoft.Cache/redisEnterprise sku.name enum (e.g. also Balanced_B10/B20/..., ComputeOptimized_X5/X10/..., MemoryOptimized_M20/M50/...), which is already a valid raw ARM value so no sku/family/capacity translation is needed (unlike the retired classic Azure Cache for Redis). Only consulted when useAzureCacheForRedis is true.')
@allowed(['Balanced_B0', 'Balanced_B5', 'MemoryOptimized_M10'])
param azureCacheForRedisTier string = 'Balanced_B0'

@description('The deployment-time choice between two secret-storage modes (see Documents/KeyVault-Implementation.html): false (default) keeps tenant SourceConnection/DestinationConfiguration secrets and the app\'s own 4 app-level secrets (jwt-signing-key etc.) on the local DataProtection-encrypted ProvisionedSecrets DB table — no Key Vault resource, no extra permission needed. true creates a dedicated RBAC-enabled Key Vault, grants the segueApp/worker Container Apps\' system-assigned managed identities (and the identity running this deployment) the Key Vault Secrets Officer role on it, creates an RSA key for DataProtection key-ring wrapping (Crypto User granted to both apps), and points KeyVault:VaultName/KeyVault:UseAzureKeyVault/DataProtection:KeyVaultKeyId at it — the app reads/writes secrets there automatically via CompositeSecretProvider/Writer, with the local DB table remaining as an automatic fallback (KeyVault:AllowConfigurationFallback). Unlike the Terraform azure environment, this template does NOT pre-seed the 4 app-level secrets — AppSecretProvisioner generates and writes them itself on first boot once the RBAC role above is in place, which keeps this template free of any secret-generation logic of its own.')
param enableTenantSecretsKeyVault bool = false

@description('Chooses whether this deployment includes a Seq container for centralized structured log viewing. false (default) — no Seq container; FHIRBridge.Api/.Gateway/.Worker log to console/Log Analytics only, same as leaving every other toggle at its default. true creates a Seq container app (datalust/seq, public image) with its own external ingress — its own https://<namePrefix>-seq.<environment>.azurecontainerapps.io URL, protected by seqAdminPassword — and points Observability:SeqServerUrl at it on segueApp and worker. All three hosts (Api, Gateway, Worker) pick this up automatically since SegueLogging (FHIRBridge.Observability) reads that same config key on every host that calls it — see src/BuildingBlocks/FHIRBridge.Observability/Logging/SegueLogging.cs.')
param enableSeq bool = false

@description('Admin password for the Seq web UI (SEQ_FIRSTRUN_ADMINPASSWORD) — required when enableSeq is true. This is the ONLY thing protecting that URL, since no other authentication is configured here. Ignored when enableSeq is false.')
@secure()
param seqAdminPassword string = ''

@description('Seq container size. Only consulted when enableSeq is true.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param seqSize string = 'Small'

@description('false (default) — segueApp is reached directly at its *.azurecontainerapps.io URL, with no inspection layer in front of it. true creates an Azure Front Door (Standard tier) profile with a WAF policy in front of segueApp — Front Door is both a global HTTP load balancer and a WAF in one resource, so this single toggle gets both. Standard (not Premium) keeps cost down and is enough to start; the one thing it does not close is that segueApp\'s own *.azurecontainerapps.io address stays technically reachable directly, bypassing Front Door, since Standard has no Private Link to hide the origin behind — that address is not published anywhere once a custom domain is set, so real-world exposure is low. See wafPolicyMode below for whether the WAF actually blocks anything, and the Front Door resources further down for the full reasoning, including why this can be created in this SAME deployment unlike the custom-domain flow above.')
param enableFrontDoorWaf bool = false

@description('Only consulted when enableFrontDoorWaf is true. Prevention (default) actually blocks requests the WAF custom rule flags. Detection only logs/scores them — the WAF exists but blocks nothing, which is a real, deliberate choice for a cautious rollout (watch its logs for false positives against this app\'s own traffic before switching), not just a lesser default. Prevention is the default here rather than the more common "start in Detection, graduate later" advice specifically because this template ships to many independent client installs with no central place for anyone to watch logs and decide when it\'s safe to flip each one — left in Detection, it would likely just stay a no-op indefinitely.')
@allowed(['Prevention', 'Detection'])
param wafPolicyMode string = 'Prevention'

@description('Only consulted when enableFrontDoorWaf is true. Requests from a single client IP per minute before the WAF\'s rate-limit custom rule flags it (see frontDoorWafPolicy below) — the baseline protection this template uses in place of Premium-only managed rule sets, which Standard_AzureFrontDoor cannot host. 300/min is generous enough not to trip up a legitimate integration polling the API; tighten it for a smaller, known client base.')
param wafRateLimitThreshold int = 300

// Granting an RBAC role needs Microsoft.Authorization/roleAssignments/write (Owner or User Access
// Administrator) on the vault/resource group — a Contributor-only account can create the vault
// itself just fine but will get an authorization error on the role assignments below specifically.
// If that happens: redeploy with enableTenantSecretsKeyVault left false, then have someone with
// sufficient rights grant these manually using the segueAppPrincipalId/workerPrincipalId
// outputs plus their own account's object ID:
//   az role assignment create --role "Key Vault Secrets Officer" --assignee <principal-id> --scope <tenantSecretsKeyVaultId output>
//   az role assignment create --role "Key Vault Crypto User" --assignee <principal-id> --scope <tenantSecretsKeyVaultId output>
// then redeploy with enableTenantSecretsKeyVault=true once those are in place.
var keyVaultSecretsOfficerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var keyVaultCryptoOfficerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '14b46e9e-c2b7-41b4-b07b-48a6ebf60603')
var keyVaultCryptoUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')

@description('Custom domain for the Segue app (e.g. app.customer.com). Leave blank to keep the auto-generated *.azurecontainerapps.io URL. REQUIRED three-deploy flow, not two, to avoid InvalidCustomHostNameValidation/RequireCustomHostnameInEnvironment: (0) first deploy with this LEFT BLANK, so the app is actually created — Azure only assigns its customDomainVerificationId once the app exists, and there is no way to know that ID in advance, so setting a domain on the very first-ever deploy of a given namePrefix always fails with "a TXT record ... was not found"; (1) once deployed, read segueAppDomainVerificationId, create CNAME (domain -> segueAppUrl hostname) + TXT asuid.<domain> = that id, wait for DNS, THEN redeploy with this domain set and bindCustomDomainCertificates=false — registers the hostname (bindingType Disabled, no cert yet); (2) redeploy again with the SAME domain and bindCustomDomainCertificates=true — creates the managed certificate (hostname already exists) then binds SniEnabled SSL.')
param segueAppCustomDomain string = ''

@description('Phase-2 flag. false (default) = register custom hostnames only (bindingType Disabled), do NOT create managed certificates. true = create managed certificates and bind SniEnabled SSL. Only set true AFTER hostnames were registered in a prior deploy AND DNS CNAME + asuid TXT have propagated. Setting true on first deploy with a new domain causes RequireCustomHostnameInEnvironment.')
param bindCustomDomainCertificates bool = false

// The param below is DELIBERATELY inert - captured here purely so a customer can note their
// intended domain while filling out this wizard, without touching Azure at all (no
// customDomains/ingress/certificate wiring, no TXT-record requirement, no risk of
// InvalidCustomHostNameValidation on a first-ever deploy). It's only echoed back in this
// template's outputs, as a plain reminder - actually activating a domain is a separate step (see
// custom-domain.bicep / containerization/azure-deploy/CUSTOM_DOMAIN_SELF_SERVICE.md), which
// re-asks for the domain and does the real work once this app already exists.

@description('Domain you intend to use for the Segue app later (e.g. app.customer.com) - purely a reminder, echoed in this deployment\'s outputs. Does not configure anything in Azure by itself; activate it afterward with custom-domain.bicep (Step 2).')
param segueAppIntendedDomain string = ''

// segue-app has no equivalent parameter: Azure Container Apps external HTTP
// ingress has no client-configurable port — it's always https://<app>.<domain> with no port
// number in the URL, regardless of targetPort. That's a genuine Container Apps platform
// constraint, not something this template can work around.

@description('Containerized Postgres container size. Meaningless when useAzurePostgresql is true — the managed Flexible Server is sized via azurePostgresqlSku/azurePostgresqlStorageMb instead.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param postgresSize string = 'Large'

@description('Azure Database for PostgreSQL Flexible Server compute/pricing tier — a curated key, not a raw Azure SKU name, so an invalid tier/SKU combination (ParameterOutOfRange) is impossible. Mapped to the actual sku.name/sku.tier pair via postgresSkuMap below. Only consulted when useAzurePostgresql is true.')
@allowed(['Burstable_B1ms', 'Burstable_B2s', 'GeneralPurpose_D2s_v3', 'GeneralPurpose_D4s_v3'])
param azurePostgresqlSku string = 'Burstable_B1ms'

@description('Azure Database for PostgreSQL Flexible Server storage size, from the platform\'s supported set (32GB is the minimum). Only consulted when useAzurePostgresql is true — storage can only be scaled up later, not down, so don\'t over-provision speculatively.')
@allowed([32768, 65536, 131072, 262144])
param azurePostgresqlStorageMb int = 32768

// Curated key -> real Azure SKU name/tier pair. Azure's ARM API requires sku.name to be a plain
// VM size (e.g. "Standard_B1ms") and sku.tier to independently match its family (Burstable /
// GeneralPurpose / MemoryOptimized) — passing a combined string like "B_Standard_B1ms" (the format
// Terraform's azurerm provider accepts and splits internally) directly as sku.name fails with
// ParameterOutOfRange. This map is the single place that translation happens.
var postgresSkuMap = {
  Burstable_B1ms: { name: 'Standard_B1ms', tier: 'Burstable' }
  Burstable_B2s: { name: 'Standard_B2s', tier: 'Burstable' }
  GeneralPurpose_D2s_v3: { name: 'Standard_D2s_v3', tier: 'GeneralPurpose' }
  GeneralPurpose_D4s_v3: { name: 'Standard_D4s_v3', tier: 'GeneralPurpose' }
}

@description('Redis container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param redisSize string = 'Medium'

@description('Segue app (Api + Gateway) container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param segueAppSize string = 'Medium'

@description('Worker container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param workerSize string = 'Small'

// Azure Container Apps' Consumption plan only accepts CPU/memory at a fixed 1:2 ratio from a
// specific set of valid pairs — arbitrary combinations are rejected at deploy time. Exposing raw
// numeric fields to the customer risks an invalid combo, so every container picks from this same
// preset ladder instead. XLarge remains available across the board for whichever container needs
// the headroom under a particular workload.
var containerSizes = {
  Small:  { cpu: json('0.25'), memory: '0.5Gi' }
  Medium: { cpu: json('0.5'),  memory: '1Gi' }
  Large:  { cpu: json('1.0'),  memory: '2Gi' }
  XLarge: { cpu: json('2.0'),  memory: '4Gi' }
}

// Applied to every resource below that supports `tags` — lets you find/filter/cost-report on
// everything this deployment created, and is what cleanup.sh|ps1's tag-based teardown mode
// matches against (this deployment doesn't have Terraform state to fall back on).
var commonTags = {
  Project: 'Segue'
  Component: 'containerization'
  Environment: namePrefix
  ManagedBy: 'Bicep'
}

var uniqueSuffix = uniqueString(resourceGroup().id, namePrefix) // always exactly 13 characters
// Storage account names cap at 24 characters (alnum only, globally unique) - "st" (2) + the
// 13-character uniqueSuffix leaves only 9 characters of headroom for namePrefix, which can be up
// to 21 characters long (createUiDefinition.json's own regex allows it). Truncating namePrefix to
// its first 9 characters here (only for this name - every other resource name below still uses
// the full namePrefix, since ACR/Container Apps/etc. have far more headroom) keeps this valid
// regardless of how long a namePrefix is chosen - a bug that surfaced with the default namePrefix
// ("segue", 10 characters) alone already being one character too many before this fix.
var storageAccountName = toLower('${take(namePrefix, 9)}st${uniqueSuffix}')

// Plain-string app names (not resource attribute lookups) so a Container App can compute its OWN
// public URL from its own name + the environment's default domain — a Container App's FQDN is
// always "<app-name>.<environment-default-domain>", and the environment's domain doesn't depend
// on any individual app, so this needs no circular self-reference.
var postgresName = '${namePrefix}-postgres'
var redisName = '${namePrefix}-redis'
var segueAppName = '${namePrefix}-app'
var workerName = '${namePrefix}-worker'
var seqName = '${namePrefix}-seq'

var hasRegistryCreds = !empty(imageRegistryUsername)
var registryConfig = hasRegistryCreds ? [
  {
    server: imageRegistryServer
    username: imageRegistryUsername
    passwordSecretRef: 'registry-password'
  }
] : []
var registrySecret = hasRegistryCreds ? [
  {
    name: 'registry-password'
    value: imageRegistryPassword
  }
] : []

// --- Tenant secrets Key Vault — only when enableTenantSecretsKeyVault is true. What the running
//     app reads/writes to continuously at runtime via CompositeSecretProvider/Writer (tenant
//     SourceConnection/DestinationConfiguration secrets, plus the 4 app-level secrets —
//     jwt-signing-key etc. — which AppSecretProvisioner self-provisions on first boot once the RBAC
//     role below is in place; this template deliberately does not pre-seed them itself, unlike the
//     Terraform azure environment, to avoid needing any secret-generation logic here). RBAC-enabled
//     (not classic access policies) so access is granted via role assignments below. ---

// Key Vault names: alnum + hyphen, <=24 chars, globally unique. Truncated the same way
// storageAccountName is above, for the same reason (namePrefix can be up to 21 chars).
var tenantSecretsKeyVaultName = toLower('${take(namePrefix, 8)}-tkv-${take(uniqueSuffix, 8)}')

resource tenantSecretsKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (enableTenantSecretsKeyVault) {
  name: tenantSecretsKeyVaultName
  location: location
  tags: commonTags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    // Without this, someone with the vault's purge permission can force-delete it immediately,
    // skipping the 7-day soft-delete window above entirely. That matters specifically because
    // phi-encryption-key (one of the 4 app-level secrets this vault holds once enabled) is never
    // rotated by design — losing the vault before its recovery window is up would have the same
    // effect as losing that key outright: every previously-encrypted execution-history row becomes
    // permanently undecryptable. This flag is irreversible once set (by Azure's own design, so it
    // can't be turned off to bypass itself) — the vault is guaranteed to survive its full retention
    // window no matter what, then auto-purges on schedule same as before.
    enablePurgeProtection: true
  }
}

// Grants the identity running this deployment permission to read/set secrets and create the
// DataProtection key below — mirrors the Terraform azure environment's identical bootstrap grant.
// Needs Owner/User Access Administrator on the resource group; see enableTenantSecretsKeyVault's
// description above for the fallback if this deployment's identity only has Contributor.
resource tenantSecretsDeployerSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, deployer().objectId, 'KeyVaultSecretsOfficer')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficerRoleId
    principalId: deployer().objectId
  }
}

// Creating a Key object (not just a Secret) needs Key Vault Crypto OFFICER — broader than what the
// app itself needs at runtime (Crypto USER, wrap/unwrap only — granted to segueApp/worker below).
resource tenantSecretsDeployerCryptoOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, deployer().objectId, 'KeyVaultCryptoOfficer')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultCryptoOfficerRoleId
    principalId: deployer().objectId
  }
}

// DataProtection key-ring protection (Documents/KeyVault-Implementation.html §3/§7's
// DataProtection:KeyVaultKeyId) — wraps the app's DataProtection key ring using this key's
// wrap/unwrap operations instead of a local certificate. Depends explicitly on the Crypto Officer
// grant above: Azure AD role-assignment propagation can lag a few seconds behind the assignment's
// own creation, and without this dependsOn, ARM could attempt to create the key before the grant
// has actually taken effect.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = if (enableTenantSecretsKeyVault) {
  parent: tenantSecretsKeyVault
  name: '${namePrefix}-dataprotection-key'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
  dependsOn: [tenantSecretsDeployerCryptoOfficer]
}

// segueApp/worker's system-assigned identities aren't known until those resources are
// declared below, but Bicep resolves resources by symbolic name regardless of file order, so these
// can live here alongside the vault they grant access to. principalType 'ServicePrincipal' avoids
// an Azure AD replication-lag failure (PrincipalNotFound) that can otherwise occur when granting a
// role to an identity created earlier in this same deployment.
resource tenantSecretsSegueAppSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, segueAppName, 'KeyVaultSecretsOfficer')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficerRoleId
    principalId: segueApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource tenantSecretsWorkerSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, workerName, 'KeyVaultSecretsOfficer')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficerRoleId
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource tenantSecretsSegueAppCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, segueAppName, 'KeyVaultCryptoUser')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultCryptoUserRoleId
    principalId: segueApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource tenantSecretsWorkerCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableTenantSecretsKeyVault) {
  name: guid(tenantSecretsKeyVaultName, workerName, 'KeyVaultCryptoUser')
  scope: tenantSecretsKeyVault
  properties: {
    roleDefinitionId: keyVaultCryptoUserRoleId
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Container Apps environment (Log Analytics is required, not optional, for Container Apps) ---

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: commonTags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env'
  location: location
  tags: commonTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// --- Persistent storage for the containerized Postgres + Redis (Container Apps are otherwise stateless) ---

@description('Replication for the storage account backing Azure Files (containerized Postgres/Redis/keys/Seq data) and, when useAzurePostgresql is false, the postgres-backups blob container. Standard_ZRS (default) synchronously replicates across 3 availability zones in the region, protecting against a single-datacenter outage — Standard_LRS keeps all copies in one datacenter and is only cheaper. Exposed as a dropdown, not hardcoded, because ZRS is not available in every Azure region — if a deploy fails on this resource with a SKU/region error, redeploy with Standard_LRS instead.')
@allowed(['Standard_ZRS', 'Standard_LRS'])
param storageRedundancy string = 'Standard_ZRS'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: commonTags
  sku: { name: storageRedundancy }
  kind: 'StorageV2'
}

// Blob-level soft-delete + versioning — separate from, and in addition to, the file-share backup
// pattern used elsewhere in this template. Only matters in practice once postgresBackupsContainer
// below actually holds something (useAzurePostgresql = false), but costs nothing to leave on
// unconditionally: an account with no blob containers just has no blobs for these policies to ever
// apply to.
resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 }
    containerDeleteRetentionPolicy: { enabled: true, days: 30 }
    isVersioningEnabled: true
  }
}

// Destination for postgresBackupJob's scheduled pg_dump uploads — see that job's own comment
// (alongside postgresApp below) for the full backup-plan reasoning. Only created for the
// containerized Postgres path; the managed path (useAzurePostgresql = true) has no use for it.
resource postgresBackupsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = if (!useAzurePostgresql) {
  parent: blobServices
  name: 'postgres-backups'
  properties: {
    publicAccess: 'None'
  }
}

// Only needed for the containerized Postgres path — Azure Database for PostgreSQL is a managed
// PaaS service with no Azure Files volume of its own. Gated the same as postgresApp below.
resource postgresDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = if (!useAzurePostgresql) {
  name: '${storageAccount.name}/default/postgres-data'
  properties: { shareQuota: 50 }
}

// Only needed for the containerized Redis path — Azure Cache for Redis is a managed PaaS service
// with no Azure Files volume of its own. Gated the same as redisApp below.
resource redisDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = if (!useAzureCacheForRedis) {
  name: '${storageAccount.name}/default/redis-data'
  properties: { shareQuota: 10 }
}

resource keysDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/keys-data'
  properties: { shareQuota: 1 }
}

// Only needed when enableSeq is true — Seq persists its log database under /data. Unlike Postgres
// (see the top-of-file comment and this deployment's own run log for why that one can't use Azure
// Files at all), Seq's storage engine hasn't been confirmed either way against the same SMB
// permission limitation — if Seq's container fails at startup with a similar "Operation not
// permitted" error, the same fix pattern (a custom local-disk-plus-backup image, following
// containerization/docker/postgres-local as a template) would need to be applied here too.
resource seqDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = if (enableSeq) {
  name: '${storageAccount.name}/default/seq-data'
  properties: { shareQuota: 10 }
}

resource postgresDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = if (!useAzurePostgresql) {
  parent: containerAppEnv
  name: 'postgres-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'postgres-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [postgresDataShare]
}

resource redisDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = if (!useAzureCacheForRedis) {
  parent: containerAppEnv
  name: 'redis-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'redis-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [redisDataShare]
}

resource keysDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppEnv
  name: 'keys-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'keys-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [keysDataShare]
}

resource seqDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = if (enableSeq) {
  parent: containerAppEnv
  name: 'seq-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'seq-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [seqDataShare]
}


// --- Segue's own database: containerized Postgres (internal only, single replica — local
//     ephemeral disk isn't safe for concurrent multi-instance Postgres either way) — only when NOT
//     using Azure Database for PostgreSQL. Replaces the SQL Server Express container this
//     deployment used before the app migrated from SQL Server to PostgreSQL. ---

resource postgresApp 'Microsoft.App/containerApps@2024-03-01' = if (!useAzurePostgresql) {
  name: postgresName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: concat([
        { name: 'postgres-password', value: postgresPassword }
      ], registrySecret)
      registries: registryConfig
      ingress: {
        external: false
        targetPort: postgresPort
        transport: 'tcp'
      }
    }
    // Generous grace period (default is much shorter) so the custom image's shutdown handler has
    // time to stop postgres cleanly AND copy PGDATA out to the backup mount before Container Apps
    // force-kills the replica — see containerization/docker/postgres-local/entrypoint.sh.
    template: {
      terminationGracePeriodSeconds: 90
      containers: [
        {
          name: 'postgres'
          // Custom image (not stock postgres:16-alpine): Postgres's own startup permission check
          // can never pass directly on Azure Files (SMB) — see this image's own Dockerfile comment.
          // This wraps the stock image with a copy-in/copy-out entrypoint that runs Postgres on
          // local (ephemeral) disk instead, using the Azure Files mount below purely as an at-rest
          // backup target restored on start / saved on graceful stop. Real durability tradeoff:
          // anything written since the last graceful shutdown is lost on a non-graceful restart —
          // accepted here since there is no supported way to give Postgres real POSIX permissions
          // on Container Apps' only persistent-storage option (confirmed: azureFile only exposes
          // accountName/accountKey/shareName/accessMode — no mount-options/NFS escape hatch).
          image: '${imageRegistryServer}/segue-postgres:${imageTag}'
          resources: containerSizes[postgresSize]
          env: [
            { name: 'POSTGRES_DB', value: 'Segue' }
            { name: 'POSTGRES_USER', value: 'segue' }
            { name: 'POSTGRES_PASSWORD', secretRef: 'postgres-password' }
            // Where the backup mount (below) is available inside the container — entrypoint.sh
            // restores PGDATA from here on start and saves back here on graceful stop. PGDATA
            // itself is left at the image's own default (local disk), not pointed at this mount.
            { name: 'PG_BACKUP_DIR', value: '/mnt/pgbackup' }
          ]
          // Postgres has no env-var port override — this passes -p through to the postgres binary
          // to listen on postgresPort, matching the ingress targetPort above. Deliberately `args`,
          // NOT `command`: `command` replaces the image's ENTRYPOINT outright, which for this image
          // is entrypoint.sh — the script that runs docker-entrypoint.sh in turn (chowns PGDATA,
          // drops from root to the unprivileged `postgres` user via gosu) AND handles the
          // backup/restore around it. Skipping it (as a `command` override would) runs the postgres
          // binary directly as root with no backup/restore at all, and postgres refuses outright
          // ("must not be run as root"), crash-looping the container. `args` instead overrides only
          // the image's CMD, leaving ENTRYPOINT (entrypoint.sh) intact — it sees the leading `-p`
          // and passes it through to docker-entrypoint.sh, which auto-prepends `postgres` itself,
          // so the effective command stays `postgres -p <port>`.
          args: [
            '-p'
            string(postgresPort)
          ]
          volumeMounts: [
            { volumeName: 'postgres-data', mountPath: '/mnt/pgbackup' }
          ]
        }
      ]
      volumes: [
        { name: 'postgres-data', storageType: 'AzureFile', storageName: postgresDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// --- Scheduled backup fallback for the containerized Postgres path (see useAzurePostgresql's own
//     description above for the full reasoning). A Container Apps Job, not a Container App — runs
//     to completion on a cron schedule instead of staying up continuously. Every run does a
//     pg_dump + gzip of postgresApp, then uploads it to the postgres-backups blob container via
//     azcopy (containerization/docker/postgres-backup) — a compensating control, not parity with
//     Flexible Server's automated backups: it bounds data loss to one interval (hourly, by
//     default) instead of "whatever was lost since the last graceful shutdown," but restoring is a
//     manual step (download the newest dump, `gunzip | psql` into a fresh database), not a
//     one-click Azure restore. Only created for the containerized path — the managed path already
//     has real backups, so this would just be redundant cost and complexity there. ---

// Account-scoped, not container-scoped, because Bicep's storage account SAS functions only offer
// account-level SAS generation — acceptable here since this account's only blob containers are the
// ones this template creates (postgres-backups), and 'b' (blob service only) doesn't reach the
// Azure Files shares living in the same account. signedExpiry is a fixed far-future date rather
// than something rotated automatically — a real long-term product would want rotation; out of
// scope for this fallback control specifically.
var postgresBackupSasProperties = {
  signedServices: 'b'
  signedResourceTypes: 'co'
  signedPermission: 'rwc'
  signedProtocol: 'https'
  signedExpiry: '2035-01-01T00:00:00Z'
}

var postgresBackupContainerSasUrl = !useAzurePostgresql
  ? '${storageAccount.properties.primaryEndpoints.blob}postgres-backups?${storageAccount.listAccountSas('2023-01-01', postgresBackupSasProperties).accountSasToken}'
  : ''

resource postgresBackupJob 'Microsoft.App/jobs@2024-03-01' = if (!useAzurePostgresql) {
  name: '${namePrefix}-postgres-backup'
  location: location
  tags: commonTags
  properties: {
    environmentId: containerAppEnv.id
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        // Hourly — matches the backup plan's stated interval. Adjust if a different RPO is agreed.
        cronExpression: '0 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      replicaTimeout: 900
      replicaRetryLimit: 1
      secrets: concat([
        { name: 'postgres-password', value: postgresPassword }
        { name: 'backup-container-sas-url', value: postgresBackupContainerSasUrl }
      ], registrySecret)
      registries: registryConfig
    }
    template: {
      containers: [
        {
          name: 'postgres-backup'
          image: '${imageRegistryServer}/segue-postgres-backup:${imageTag}'
          resources: containerSizes.Small
          env: [
            // postgresName is the same predictable internal hostname postgresApp is reached at
            // elsewhere in this template (see segueDbConnectionString) — Container Apps Jobs
            // share the environment's internal DNS with its Container Apps.
            { name: 'POSTGRES_HOST', value: postgresName }
            { name: 'POSTGRES_PORT', value: string(postgresPort) }
            { name: 'POSTGRES_USER', value: 'segue' }
            { name: 'POSTGRES_DB', value: 'Segue' }
            { name: 'POSTGRES_PASSWORD', secretRef: 'postgres-password' }
            { name: 'BACKUP_CONTAINER_SAS_URL', secretRef: 'backup-container-sas-url' }
          ]
        }
      ]
    }
  }
  dependsOn: [postgresApp, postgresBackupsContainer]
}

// --- Segue's own database: Azure Database for PostgreSQL Flexible Server — only when
//     useAzurePostgresql is true. No VNet in this deployment (Container Apps here use the
//     platform's own managed networking, not a customer VNet), so this uses public network access
//     + a firewall rule allowing Azure-internal traffic, rather than private VNet integration —
//     the simplest setup that still keeps the server unreachable from the public internet by
//     anything except Azure's own services (and, indirectly, this environment's Container Apps). ---

var postgresManagedAdminLogin = 'segueadmin'

resource postgresFlexibleServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = if (useAzurePostgresql) {
  name: '${namePrefix}-pg'
  location: location
  tags: commonTags
  sku: postgresSkuMap[azurePostgresqlSku]
  properties: {
    version: '16'
    administratorLogin: postgresManagedAdminLogin
    administratorLoginPassword: postgresPassword
    storage: {
      storageSizeGB: azurePostgresqlStorageMb / 1024
    }
  }
}

resource postgresFlexibleServerFirewallRule 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (useAzurePostgresql) {
  parent: postgresFlexibleServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource postgresFlexibleServerDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = if (useAzurePostgresql) {
  parent: postgresFlexibleServer
  name: 'Segue'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Single source of truth for both segueApp's and workerApp's ConnectionStrings__FHIRBridgeDb.
// Azure Database for PostgreSQL requires SSL by default and presents a real CA-trusted
// certificate (unlike the containerized path's plain internal-network-only connection, which
// relies on Container Apps' network isolation instead of TLS — matching how the containerized
// postgres/redis paths are already "internal only, no external exposure" by design).
var segueDbConnectionString = useAzurePostgresql
  ? 'Host=${postgresFlexibleServer.?properties.fullyQualifiedDomainName};Port=5432;Database=${postgresFlexibleServerDatabase.?name};Username=${postgresManagedAdminLogin};Password=${postgresPassword};Ssl Mode=Require;'
  : 'Host=${postgresName};Port=${postgresPort};Database=Segue;Username=segue;Password=${postgresPassword};'

// --- Redis: Azure Managed Redis (Microsoft.Cache/redisEnterprise) — only when
//     useAzureCacheForRedis is true. No container, no Azure Files volume, no self-signed TLS cert
//     to generate/bake into an image — Azure issues a normal CA-trusted certificate, so
//     FHIRBridge.Api/.Worker's ValidateRedisServerCertificate accepts it automatically as long as
//     Redis:TrustedCertificateThumbprint is left unset (see the env wiring below — that value is
//     only ever emitted for the containerized path). minimumTlsVersion 1.2 matches what the app
//     already requires (ConnectionStrings:Redis must include ssl=true outside Development).
//     clientProtocol 'Encrypted' is the TLS equivalent for this resource type. clusteringPolicy
//     'EnterpriseCluster' (not 'OSSCluster') keeps a single logical endpoint that proxies to shards
//     internally, so the app's plain StackExchange.Redis connection string keeps working unchanged
//     — 'OSSCluster' would require a cluster-aware client that handles MOVED redirections, which
//     this codebase's Redis usage was never written for (see the classic-to-managed migration notes
//     at https://aka.ms/redis/migrate/understand). evictionPolicy 'VolatileLRU' matches the default
//     classic Azure Cache for Redis used (this codebase's Redis keys — token cache entries, PKCE
//     codes — are all TTL-bound, so evicting under memory pressure is safe and preferable to
//     'NoEviction' rejecting writes with an OOM error). ---

resource azureManagedRedis 'Microsoft.Cache/redisEnterprise@2025-04-01' = if (useAzureCacheForRedis) {
  name: '${namePrefix}-cache'
  location: location
  tags: commonTags
  sku: {
    name: azureCacheForRedisTier
  }
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: 'Enabled'
  }
}

resource azureManagedRedisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = if (useAzureCacheForRedis) {
  parent: azureManagedRedis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'VolatileLRU'
  }
}

// Single source of truth for both segueApp's and workerApp's ConnectionStrings__Redis —
// resolves to whichever of the two Redis resources useAzureCacheForRedis actually created.
// abortConnect=false matches StackExchange.Redis's usual recommended default for a managed service
// (retries instead of failing fast on a transient connect issue); the containerized path doesn't
// set it, matching its pre-existing behavior. Port is always 10000 for Azure Managed Redis (both
// TLS and non-TLS traffic use the same port, unlike classic Azure Cache for Redis's separate
// 6379/6380) — see azureManagedRedisDatabase's port property above.
var redisConnectionString = useAzureCacheForRedis
  ? '${azureManagedRedis.?properties.hostName}:10000,password=${azureManagedRedisDatabase.listKeys().primaryKey},ssl=true,abortConnect=false'
  : '${redisName}:${redisPort},password=${redisPassword},ssl=true'

// --- Redis: containerized (internal only, single replica) — only when NOT using Azure Cache for
//     Redis ---

resource redisApp 'Microsoft.App/containerApps@2024-03-01' = if (!useAzureCacheForRedis) {
  name: redisName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: registrySecret
      registries: registryConfig
      ingress: {
        external: false
        targetPort: redisPort
        transport: 'tcp'
      }
    }
    template: {
      containers: [
        {
          name: 'redis'
          // Custom image (not stock redis:7-alpine): FHIRBridge.Api/.Worker refuse a plaintext
          // Redis connection outside Development (HIPAA #15), and stock Redis has no TLS
          // configured at all. See containerization/docker/redis-tls/Dockerfile.
          image: '${imageRegistryServer}/segue-redis:${imageTag}'
          resources: containerSizes[redisSize]
          // --port 0 disables the plaintext port entirely — --tls-port is the only one Redis
          // listens on. --tls-auth-clients no means server-side TLS + --requirepass, not mutual
          // TLS (no client certificate required) — matches ConnectionStrings__Redis's "ssl=true"
          // (no client cert options) on segueApp/workerApp below.
          command: [
            'redis-server'
            '--tls-port'
            string(redisPort)
            '--port'
            '0'
            '--tls-cert-file'
            '/certs/redis.crt'
            '--tls-key-file'
            '/certs/redis.key'
            '--tls-auth-clients'
            'no'
            '--requirepass'
            redisPassword
          ]
          volumeMounts: [
            { volumeName: 'redis-data', mountPath: '/data' }
          ]
        }
      ]
      volumes: [
        { name: 'redis-data', storageType: 'AzureFile', storageName: redisDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// --- Seq (structured log viewing) — only when enableSeq is true. Public image, no custom build
//     needed. External ingress deliberately: unlike Postgres/Redis (internal-only, reached only by
//     segueApp/worker), Seq exists specifically so a human can browse to it and monitor logs —
//     an internal-only Seq would have no way in from outside the Container Apps environment. ---

resource seqApp 'Microsoft.App/containerApps@2024-03-01' = if (enableSeq) {
  name: seqName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: [
        { name: 'seq-admin-password', value: seqAdminPassword }
      ]
      ingress: {
        external: true
        targetPort: 80
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'seq'
          image: 'datalust/seq:latest'
          resources: containerSizes[seqSize]
          env: [
            { name: 'ACCEPT_EULA', value: 'Y' }
            { name: 'SEQ_FIRSTRUN_ADMINPASSWORD', secretRef: 'seq-admin-password' }
          ]
          volumeMounts: [
            { volumeName: 'seq-data', mountPath: '/data' }
          ]
        }
      ]
      volumes: [
        { name: 'seq-data', storageType: 'AzureFile', storageName: seqDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// --- Custom domains (optional, per app) ---
//
// Azure managed certificates require the hostname to ALREADY exist on a Container App in the
// environment (error RequireCustomHostnameInEnvironment if not). Referencing certificateId from
// the Container App also makes ARM create the cert BEFORE the app update — so a single-shot
// "domain + cert + SniEnabled" deploy fails on a new hostname.
//
// Correct sequence (bindCustomDomainCertificates flag):
//   Phase 1 (bind=false): customDomains with bindingType Disabled, no cert resources.
//   Client creates DNS (CNAME + asuid TXT) using verification outputs.
//   Phase 2 (bind=true): create managedCertificates (hostname already on live app), then update
//   customDomains to SniEnabled with certificateId.
//
// NOTE: managedCertificates apiVersion must succeed on a real subscription after Phase 1+DNS.

resource segueAppManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(segueAppCustomDomain) && bindCustomDomainCertificates) {
  parent: containerAppEnv
  name: '${segueAppName}-cert'
  location: location
  properties: {
    subjectName: segueAppCustomDomain
    domainControlValidation: 'CNAME'
  }
}

// --- Segue app (Api + Gateway in one image), public ---

resource segueApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: segueAppName
  location: location
  tags: commonTags
  // System-assigned so DefaultAzureCredential (CompositeSecretProvider/Writer) can authenticate to
  // the tenant secrets Key Vault with no credential material to manage. Added unconditionally —
  // harmless when enableTenantSecretsKeyVault is false, and this identity may be reused for other
  // Azure resource access later.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: concat([
        { name: 'postgres-password', value: postgresPassword }
        { name: 'jwt-signing-key', value: jwtSigningKey }
      ], registrySecret)
      registries: registryConfig
      ingress: {
        external: true
        targetPort: 80
        transport: 'auto'
        // Phase 1: Disabled registers the hostname without a cert (required before managed cert).
        // Phase 2: SniEnabled + certificateId after bindCustomDomainCertificates=true.
        customDomains: !empty(segueAppCustomDomain) ? [
          bindCustomDomainCertificates ? {
            name: segueAppCustomDomain
            certificateId: segueAppManagedCert.id
            bindingType: 'SniEnabled'
          } : {
            name: segueAppCustomDomain
            bindingType: 'Disabled'
          }
        ] : []
      }
    }
    template: {
      containers: [
        {
          name: 'segue-app'
          image: '${imageRegistryServer}/segue-app:${imageTag}'
          resources: containerSizes[segueAppSize]
          // Redis__TrustedCertificateThumbprint only applies to the containerized path's
          // self-signed cert — Azure Cache for Redis presents a normal CA-trusted certificate,
          // which ValidateRedisServerCertificate accepts on its own when this is left unset.
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__FHIRBridgeDb', value: segueDbConnectionString }
            { name: 'Database__Provider', value: 'PostgreSql' }
            { name: 'ConnectionStrings__Redis', value: redisConnectionString }
            { name: 'Authentication__SigningKey', secretRef: 'jwt-signing-key' }
            { name: 'DataProtection__KeyRingPath', value: '/app/keys' }
            // Gateway proxies /api to the Api process in this same container (entrypoint binds Api on loopback :5000).
            { name: 'ApiBaseUrl', value: 'http://127.0.0.1:5000/' }
            { name: 'AllowedHosts', value: '*' }
            { name: 'Swagger__Enabled', value: 'true' }
          ], !empty(segueAppCustomDomain) ? [
            // The OAuth redirect_uri (OAuthController.BuildCallbackUri et al.) is registered verbatim with
            // each EHR — it must be exactly this fixed, known-correct value regardless of what Host/Scheme
            // Front Door's WAF (which overrides originHostHeader to this app's own default FQDN — see
            // frontDoorOrigin below) or Container Apps ingress present to the app. Only set when a custom
            // domain is actually configured: direct-to-container access with no WAF/Front Door in front has
            // no proxy lying about the Host header, so Request.Scheme/Host is already correct there and the
            // app's own fallback handles it without this override. This only seeds the INITIAL value
            // (SystemSettingsSeeder) — an operator can later correct it live from Settings > System
            // Settings > OAuth (e.g. after moving to a new custom domain) with no redeploy needed.
            { name: 'OAuth__PublicBaseUrl', value: 'https://${segueAppCustomDomain}' }
          ] : [], !useAzureCacheForRedis ? [
            { name: 'Redis__TrustedCertificateThumbprint', value: redisTrustedCertificateThumbprint }
          ] : [], enableTenantSecretsKeyVault ? [
            { name: 'KeyVault__UseAzureKeyVault', value: 'true' }
            { name: 'KeyVault__AllowConfigurationFallback', value: 'true' }
            { name: 'KeyVault__VaultName', value: tenantSecretsKeyVault.?properties.vaultUri ?? '' }
            { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKey.?properties.keyUriWithVersion ?? '' }
          ] : [], enableSeq ? [
            { name: 'Observability__SeqServerUrl', value: 'http://${seqName}' }
          ] : [])
          volumeMounts: [
            { volumeName: 'keys-data', mountPath: '/app/keys' }
          ]
        }
      ]
      volumes: [
        { name: 'keys-data', storageType: 'AzureFile', storageName: keysDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: !useAzurePostgresql
    ? (!useAzureCacheForRedis ? [postgresApp, redisApp] : [postgresApp])
    : (!useAzureCacheForRedis ? [redisApp] : [])
}

// --- Worker (no ingress) ---

resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: workerName
  location: location
  tags: commonTags
  // See the identical block on segueApp for why this exists.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: registrySecret
      registries: registryConfig
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${imageRegistryServer}/segue-worker:${imageTag}'
          resources: containerSizes[workerSize]
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__FHIRBridgeDb', value: segueDbConnectionString }
            { name: 'Database__Provider', value: 'PostgreSql' }
            { name: 'ConnectionStrings__Redis', value: redisConnectionString }
            { name: 'RuntimeWorker__Enabled', value: 'true' }
            { name: 'Messaging__Provider', value: 'InMemory' }
          ], !useAzureCacheForRedis ? [
            { name: 'Redis__TrustedCertificateThumbprint', value: redisTrustedCertificateThumbprint }
          ] : [], enableTenantSecretsKeyVault ? [
            { name: 'KeyVault__UseAzureKeyVault', value: 'true' }
            { name: 'KeyVault__AllowConfigurationFallback', value: 'true' }
            { name: 'KeyVault__VaultName', value: tenantSecretsKeyVault.?properties.vaultUri ?? '' }
            { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKey.?properties.keyUriWithVersion ?? '' }
          ] : [], enableSeq ? [
            { name: 'Observability__SeqServerUrl', value: 'http://${seqName}' }
          ] : [])
        }
      ]
      // segueApp is a head start, not a guarantee: both it and worker auto-migrate
      // FHIRBridgeDb on boot and can race on the initial CREATE DATABASE on a fresh database.
      // Container Apps replaces crashed replicas automatically, which turns a lost race into a
      // self-healing retry (see the "migration race" bug writeup in the containerization guide).
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: !useAzurePostgresql
    ? (!useAzureCacheForRedis ? [postgresApp, redisApp, segueApp] : [postgresApp, segueApp])
    : (!useAzureCacheForRedis ? [redisApp, segueApp] : [segueApp])
}

// --- Front Door (Standard) + WAF — only when enableFrontDoorWaf is true. Fronts segueApp
//     with Microsoft's global HTTP load balancer plus a WAF policy, closing the gap where the
//     app's public ingress otherwise has no inspection layer in front of it at all — relevant
//     since this app processes PHI. Unlike the custom-domain flow above, this can be created in
//     THIS SAME deployment: Front Door only needs segueApp's FQDN as a one-way origin
//     reference, not the circular self-reference that binding a domain to segueApp itself
//     would be (see the custom-domain comment above for why THAT one genuinely needs a separate
//     deployment).
//
//     Standard tier, not Premium — cheaper, and enough to start. Two things Premium would add,
//     both left as accepted gaps here:
//       - Managed rule sets (Microsoft's Default Rule Set) are a Premium-only feature — the WAF
//         policy API rejects `managedRules` outright on a Standard_AzureFrontDoor policy. So this
//         WAF carries a hand-written custom rule (per-client-IP rate limiting, see
//         frontDoorWafPolicy below) instead of OWASP-style managed rules. That's real but partial
//         protection, not equivalent to Premium's managed rule sets — a client whose compliance
//         review requires managed WAF rules needs to upgrade both frontDoorProfile and
//         frontDoorWafPolicy to Premium_AzureFrontDoor.
//       - Private Link origin support: segueApp's own *.azurecontainerapps.io address stays
//         technically reachable directly, bypassing Front Door/the WAF, since there's no Private
//         Link to hide it behind. It isn't published anywhere once a custom domain is set, so
//         real-world exposure is low — if that gap needs fully closing, the fix is either
//         upgrading to Premium + Private Link, or an app-side check that rejects requests missing
//         a header Front Door injects; neither is done here.
//
//     WAF policy mode (Prevention/Detection) is wafPolicyMode above, defaulting to Prevention —
//     applies to the custom rule below the same way it would to managed rules: Detection logs
//     matches without blocking. See that parameter's own description for why this template
//     doesn't default to the more commonly advised "start in Detection, graduate later" pattern.

var wafPolicyName = toLower('${replace(namePrefix, '-', '')}wafpolicy')
var frontDoorEndpointName = toLower('${namePrefix}-${take(uniqueSuffix, 8)}')

resource frontDoorWafPolicy 'Microsoft.Network/frontdoorWebApplicationFirewallPolicies@2022-05-01' = if (enableFrontDoorWaf) {
  name: wafPolicyName
  location: 'global'
  tags: commonTags
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
  properties: {
    policySettings: {
      enabledState: 'Enabled'
      mode: wafPolicyMode
    }
    // Standard_AzureFrontDoor rejects `managedRules` entirely (BadRequest: "does not support
    // ManagedRules") — Microsoft's Default Rule Set is Premium-only. This custom rule is the
    // Standard-tier substitute: it rate-limits per client IP rather than pattern-matching request
    // content, which is a real but narrower control than a managed rule set. The `IPMatch` against
    // 0.0.0.0/0 and ::/0 is the standard idiom for "match every request" — matchConditions can't be
    // empty, and rate limiting has no per-request content to match on anyway.
    customRules: {
      rules: [
        {
          name: 'RateLimitPerClientIp'
          priority: 100
          enabledState: 'Enabled'
          ruleType: 'RateLimitRule'
          rateLimitDurationInMinutes: 1
          rateLimitThreshold: wafRateLimitThreshold
          matchConditions: [
            {
              matchVariable: 'RemoteAddr'
              operator: 'IPMatch'
              negateCondition: false
              matchValue: ['0.0.0.0/0', '::/0']
            }
          ]
          action: 'Block'
        }
      ]
    }
  }
}

resource frontDoorProfile 'Microsoft.Cdn/profiles@2024-02-01' = if (enableFrontDoorWaf) {
  name: '${namePrefix}-afd'
  location: 'global'
  tags: commonTags
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
}

resource frontDoorEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = if (enableFrontDoorWaf) {
  parent: frontDoorProfile
  name: frontDoorEndpointName
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

resource frontDoorOriginGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = if (enableFrontDoorWaf) {
  parent: frontDoorProfile
  name: '${namePrefix}-app-origin-group'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
    }
    healthProbeSettings: {
      // Matches the health-check path the AWS ALB target group already uses (environments/aws/alb.tf) —
      // same app, same endpoint, one consistent convention across cloud environments.
      probePath: '/health'
      probeRequestType: 'GET'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 30
    }
  }
}

// hostName/originHostHeader both point at segueApp's own FQDN — Container Apps ingress
// validates the Host header against the app's registered hostnames, so these have to match for
// Front Door's forwarded requests to actually reach it.
resource frontDoorOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = if (enableFrontDoorWaf) {
  parent: frontDoorOriginGroup
  name: '${namePrefix}-app-origin'
  properties: {
    hostName: segueApp.properties.configuration.ingress.fqdn
    originHostHeader: segueApp.properties.configuration.ingress.fqdn
    httpPort: 80
    httpsPort: 443
    priority: 1
    weight: 1000
  }
}

resource frontDoorRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = if (enableFrontDoorWaf) {
  parent: frontDoorEndpoint
  name: '${namePrefix}-app-route'
  properties: {
    originGroup: {
      id: frontDoorOriginGroup.id
    }
    supportedProtocols: ['Https']
    patternsToMatch: ['/*']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
  }
  dependsOn: [frontDoorOrigin]
}

resource frontDoorSecurityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = if (enableFrontDoorWaf) {
  parent: frontDoorProfile
  name: '${namePrefix}-app-security-policy'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: {
        id: frontDoorWafPolicy.id
      }
      associations: [
        {
          domains: [
            { id: frontDoorEndpoint.id }
          ]
          patternsToMatch: ['/*']
        }
      ]
    }
  }
  dependsOn: [frontDoorRoute]
}

output segueAppUrl string = 'https://${segueAppName}.${containerAppEnv.properties.defaultDomain}'

@description('Populated only when enableFrontDoorWaf is true. The actual public entry point once Front Door + WAF is enabled — send traffic here, not to segueAppUrl directly, so the WAF actually inspects it. segueAppUrl above still works underneath (Standard tier has no Private Link to hide it) — a documented, accepted tradeoff, see the comment above the Front Door resources.')
output frontDoorEndpointUrl string = enableFrontDoorWaf ? 'https://${frontDoorEndpoint.?properties.hostName ?? ''}' : ''

@description('true when the containerized Postgres path is active — meaning postgresBackupJob is running hourly pg_dump backups to the postgres-backups blob container as a compensating control. false means useAzurePostgresql is active instead, which has real automated backups from Azure directly and needs no such job.')
output postgresScheduledBackupActive bool = !useAzurePostgresql

@description('Populated only when enableSeq is true. Log into this with the seqAdminPassword you set to browse structured logs from Api/Gateway/Worker.')
output seqUrl string = enableSeq ? 'https://${seqApp.?properties.configuration.ingress.fqdn ?? ''}' : ''

output redisMode string = useAzureCacheForRedis ? 'azure-cache' : 'container'

@description('Populated only when useAzureCacheForRedis is true. Same host ConnectionStrings:Redis points the app at — useful for connecting a client (e.g. redis-cli / RedisInsight) directly for debugging.')
output azureCacheForRedisHostname string = useAzureCacheForRedis ? (azureManagedRedis.?properties.hostName ?? '') : ''

@description('Populated only when enableTenantSecretsKeyVault is true. Scope to use in a manual "az role assignment create --role ... --scope <this>" if the role assignments above fail for lack of Owner/User Access Administrator rights.')
output tenantSecretsKeyVaultId string = enableTenantSecretsKeyVault ? tenantSecretsKeyVault.id : ''

@description('Populated only when enableTenantSecretsKeyVault is true. Use with "az keyvault secret list --vault-name <this>" to see what the app has provisioned there so far.')
output tenantSecretsKeyVaultName string = enableTenantSecretsKeyVault ? tenantSecretsKeyVaultName : ''

@description('Populated only when enableTenantSecretsKeyVault is true. Same value the app receives as KeyVault:VaultName.')
output tenantSecretsKeyVaultUri string = enableTenantSecretsKeyVault ? (tenantSecretsKeyVault.?properties.vaultUri ?? '') : ''

@description('Populated only when enableTenantSecretsKeyVault is true. Same value the app receives as DataProtection:KeyVaultKeyId.')
output dataProtectionKeyId string = enableTenantSecretsKeyVault ? (dataProtectionKey.?properties.keyUriWithVersion ?? '') : ''

@description('System-assigned managed identity principal ID for the segueApp Container App. Use as --assignee for the manual role-assignment fallback above.')
output segueAppPrincipalId string = segueApp.identity.principalId

@description('System-assigned managed identity principal ID for the worker Container App. Use as --assignee for the manual role-assignment fallback above.')
output workerPrincipalId string = workerApp.identity.principalId

// Always populated regardless of whether segueAppCustomDomain is set — add a CNAME (your
// domain -> segueAppUrl's hostname) and a TXT record named asuid.<your domain> with this
// value at your DNS provider. Flow: Phase 1 deploy with domain + bindCustomDomainCertificates=false
// (hostname registered), create DNS, wait; Phase 2 redeploy with bindCustomDomainCertificates=true.
output segueAppDomainVerificationId string = segueApp.properties.customDomainVerificationId

output segueAppCustomDomainUrl string = !empty(segueAppCustomDomain) ? 'https://${segueAppCustomDomain}' : ''
output bindCustomDomainCertificates bool = bindCustomDomainCertificates
output customDomainPhase string = empty(segueAppCustomDomain) ? 'none' : (bindCustomDomainCertificates ? 'ssl-bound-or-binding' : 'hostname-only-set-dns-then-redeploy-with-bind-true')

// Purely a reminder of whatever was typed into the (inert) intended-domain field above - nothing
// in this deployment acts on this value. Use custom-domain.bicep (Step 2) to actually activate
// a domain once this deployment has finished.
output intendedCustomDomains object = {
  segueApp: segueAppIntendedDomain
}
output customDomainActivationNote string = empty(segueAppIntendedDomain) ? '' : 'Domain names entered above are not yet active - they are recorded here purely for your reference. To actually route traffic to a custom domain, complete Step 2 (custom-domain.bicep) now that this deployment has finished.'

// Whichever Postgres resources actually exist for the active path — firewall rule + database
// (children) before their parent Flexible Server for the managed path, or just the container app
// for the containerized path (its own data storage/share are folded into the storage/share groups
// below, in the same relative position sqlDataStorage/sqlDataShare held before this migration).
var postgresManifestItems = useAzurePostgresql ? [
  postgresFlexibleServerFirewallRule.id
  postgresFlexibleServerDatabase.id
  postgresFlexibleServer.id
] : [
  postgresBackupJob.id
  postgresApp.id
]
var postgresStorageManifestItems = useAzurePostgresql ? [] : [postgresDataStorage.id]
var postgresShareManifestItems = useAzurePostgresql ? [] : [postgresDataShare.id]
var postgresBackupManifestItems = useAzurePostgresql ? [] : [postgresBackupsContainer.id]

// Whichever Redis resource actually exists for the active path — the managed cache instance, or
// the container app plus its own data storage/share (folded into the storage/share groups below).
var redisManifestItems = useAzureCacheForRedis ? [
  azureManagedRedisDatabase.id
  azureManagedRedis.id
] : [redisApp.id]
var redisStorageManifestItems = useAzureCacheForRedis ? [] : [redisDataStorage.id]
var redisShareManifestItems = useAzureCacheForRedis ? [] : [redisDataShare.id]

var seqManifestItems = enableSeq ? [seqApp.id] : []
var seqStorageManifestItems = enableSeq ? [seqDataStorage.id] : []
var seqShareManifestItems = enableSeq ? [seqDataShare.id] : []

// Children before parents, matching the pattern of every other manifest group above — the security
// policy/route reference the WAF policy/origin group respectively, so list them first.
var frontDoorManifestItems = enableFrontDoorWaf ? [
  frontDoorSecurityPolicy.id
  frontDoorRoute.id
  frontDoorOrigin.id
  frontDoorOriginGroup.id
  frontDoorEndpoint.id
  frontDoorProfile.id
  frontDoorWafPolicy.id
] : []

// Every resource this deployment created, in a dependency-safe DELETION order (children before
// their parents — e.g. the Container Apps before the environment they run in). Extracted to a var
// (not left inline in the output below) so deploymentManifest's own "resources" property can reuse
// the exact same list without duplicating this expression — Bicep outputs can't reference each
// other directly.
var resourceManifestItems = concat(
  postgresManifestItems,
  redisManifestItems,
  seqManifestItems,
  [
    segueApp.id
    workerApp.id
  ],
  postgresStorageManifestItems,
  redisStorageManifestItems,
  seqStorageManifestItems,
  [
    keysDataStorage.id
  ],
  postgresShareManifestItems,
  redisShareManifestItems,
  seqShareManifestItems,
  postgresBackupManifestItems,
  [
    keysDataShare.id
    containerAppEnv.id
    storageAccount.id
    logAnalytics.id
  ],
  // The Key resource and role assignments are children/scoped to the vault and get deleted
  // automatically when it does — only the vault itself needs listing here.
  enableTenantSecretsKeyVault ? [tenantSecretsKeyVault.id] : [],
  frontDoorManifestItems
)

// Azure keeps this output in the deployment's own history (`az deployment group show --name main
// --query properties.outputs.resourceManifest.value`) indefinitely, with no extra resource needed
// to store it — cleanup.sh|ps1 reads this first and deletes exactly these IDs in order, falling
// back to a tag-based scan only if this deployment record isn't found (e.g. deployment history was
// purged).
output resourceManifest array = resourceManifestItems

@description('Every non-secret setting this deployment was run with, plus the same resourceManifest list above, in one object — read back by containerization/scripts/update-application.ps1|sh and upgrade-resources.ps1|sh (via `az deployment group show --name main --query properties.outputs.deploymentManifest.value`) so a later version update or resize does not need these re-entered and cannot silently drift back to main.bicep\'s defaults. Deliberately excludes every @secure() parameter (postgresPassword, jwtSigningKey, redisPassword, image registry credentials) — Azure never exposes those back through this or any other mechanism, so those 3 always have to be supplied fresh by whoever runs update-application/upgrade-resources. schemaVersion exists so a future breaking change to this shape (e.g. the planned Managed<->Containerized migration tooling) can detect and handle an older manifest instead of misreading it.')
output deploymentManifest object = {
  schemaVersion: 1
  namePrefix: namePrefix
  location: location
  imageTag: imageTag
  imageRegistryServer: imageRegistryServer
  useAzurePostgresql: useAzurePostgresql
  azurePostgresqlSku: azurePostgresqlSku
  azurePostgresqlStorageMb: azurePostgresqlStorageMb
  postgresSize: postgresSize
  useAzureCacheForRedis: useAzureCacheForRedis
  azureCacheForRedisTier: azureCacheForRedisTier
  redisSize: redisSize
  segueAppSize: segueAppSize
  workerSize: workerSize
  enableTenantSecretsKeyVault: enableTenantSecretsKeyVault
  storageRedundancy: storageRedundancy
  enableSeq: enableSeq
  seqSize: seqSize
  enableFrontDoorWaf: enableFrontDoorWaf
  wafPolicyMode: wafPolicyMode
  resources: resourceManifestItems
}
