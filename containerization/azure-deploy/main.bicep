// FHIRBridge — single-deployment Azure Container Apps template.
//
// This is the "easy install" counterpart to ../terraform/environments/azure: same 5-container
// topology (fhirbridge-app, demo-app, sqlserver, redis, worker), same Container Apps Environment
// design, but expressed as one Bicep template so it can be deployed with a single command or a
// single "Deploy to Azure" button click, instead of a multi-step `terraform apply`.
//
// Resource-group scoped: the customer picks/creates the resource group in the Azure Portal's
// deployment wizard (or via -g on the CLI) — this template does not create the resource group
// itself.
//
// IMPORTANT — image publishing is a prerequisite, not something this template does: a genuinely
// one-click deploy requires the 3 custom images (fhirbridge-app, demo-app, fhirbridge-worker) to
// already exist in a registry the customer's Container Apps can reach BEFORE they click deploy.
// Build and push them once (see containerization/scripts/build-images.sh|ps1) to whatever registry
// you control, then point imageRegistryServer/imageTag at that release. See README.md in this
// folder for the full publishing + one-click deploy story.

@description('Short name used to build every resource name in this deployment.')
param namePrefix string = 'fhirbridge'

@description('Azure region for every resource. Defaults to the resource group\'s own region.')
param location string = resourceGroup().location

@description('SQL Server SA password. Must satisfy SQL Server\'s complexity policy.')
@secure()
param sqlSaPassword string

@description('FHIRBridge.Api\'s Authentication:SigningKey (HS256). At least 32 random characters.')
@secure()
@minLength(32)
param jwtSigningKey string

@description('Registry the 3 custom images were published to (e.g. myregistry.azurecr.io, or ghcr.io/your-org for a public GHCR package).')
param imageRegistryServer string

@description('Tag the 3 custom images were published under.')
param imageTag string = 'latest'

@description('Registry username. Leave blank if the registry allows anonymous/public pull (e.g. a public GHCR package) — no registry credentials are configured in that case.')
@secure()
param imageRegistryUsername string = ''

@description('Registry password/token. Leave blank alongside imageRegistryUsername for anonymous/public pull.')
@secure()
param imageRegistryPassword string = ''

@description('Port SQL Server Express listens on (internal-only — reached only by the other Container Apps in this environment, never externally). Passed to the container as MSSQL_TCP_PORT since ingress targetPort alone does not change what SQL Server itself listens on.')
param sqlPort int = 1433

@description('Port Redis listens on (internal-only). Passed to the container via a redis-server --port override, since Redis has no env-var port setting.')
param redisPort int = 6379

@description('Password Redis requires (--requirepass) — defense-in-depth on top of network isolation. Passed as a plain container command argument (Redis has no env-var equivalent and Container Apps command arguments have no secretRef option), so it is visible to anyone with read access to this Container App\'s configuration — same exposure level as any other command argument.')
@secure()
param redisPassword string

@description('Custom domain for the FHIRBridge app (e.g. app.customer.com). Leave blank (default) to keep using the auto-generated *.azurecontainerapps.io URL. Requires a two-phase deploy: (1) deploy with this left blank, read the fhirbridgeAppDomainVerificationId output, add a CNAME (this domain -> fhirbridgeAppUrl\'s hostname) and a TXT record named asuid.<this domain> (value = that output) at your DNS provider, wait for propagation; (2) set this parameter and redeploy — this provisions a free Azure-managed certificate (fails if DNS isn\'t ready yet) and binds the domain.')
param fhirbridgeAppCustomDomain string = ''

@description('Custom domain for the Demo app. Same two-phase flow as fhirbridgeAppCustomDomain — see the demoAppDomainVerificationId output.')
param demoAppCustomDomain string = ''

// fhirbridge-app / demo-app have no equivalent parameter: Azure Container Apps external HTTP
// ingress has no client-configurable port — it's always https://<app>.<domain> with no port
// number in the URL, regardless of targetPort. That's a genuine Container Apps platform
// constraint, not something this template can work around.

@description('SQL Server container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param sqlServerSize string = 'Large'

@description('Redis container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param redisSize string = 'Medium'

@description('FHIRBridge app (Api + Gateway) container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param fhirbridgeAppSize string = 'Medium'

@description('Demo app container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param demoAppSize string = 'Small'

@description('Worker container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param workerSize string = 'Small'

// Azure Container Apps' Consumption plan only accepts CPU/memory at a fixed 1:2 ratio from a
// specific set of valid pairs — arbitrary combinations are rejected at deploy time. Exposing raw
// numeric fields to the customer risks an invalid combo, so every container picks from this same
// preset ladder instead. XLarge exists mainly for sqlServerSize: SQL Server's first-run
// initialization can spike memory harder than steady-state, and 2Gi (Large) has been observed
// hitting an OOM kill (container exit code 137) during that one-time setup.
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
  Project: 'FHIRBridge'
  Component: 'containerization'
  Environment: namePrefix
  ManagedBy: 'Bicep'
}

var uniqueSuffix = uniqueString(resourceGroup().id, namePrefix)
var storageAccountName = toLower('${namePrefix}st${uniqueSuffix}') // Storage account: alnum only, <=24 chars, globally unique

// Plain-string app names (not resource attribute lookups) so a Container App can compute its OWN
// public URL from its own name + the environment's default domain — a Container App's FQDN is
// always "<app-name>.<environment-default-domain>", and the environment's domain doesn't depend
// on any individual app, so this needs no circular self-reference.
var sqlServerName = '${namePrefix}-sqlserver'
var redisName = '${namePrefix}-redis'
var fhirbridgeAppName = '${namePrefix}-app'
var demoAppName = '${namePrefix}-demo-app'
var workerName = '${namePrefix}-worker'

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

// --- Persistent storage for SQL Server + Redis (Container Apps are otherwise stateless) ---

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: commonTags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
}

resource sqlDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/sql-data'
  properties: { shareQuota: 50 }
}

resource redisDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/redis-data'
  properties: { shareQuota: 10 }
}

resource sqlDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppEnv
  name: 'sql-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'sql-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [sqlDataShare]
}

resource redisDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
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

// --- SQL Server Express (internal only, single replica — Azure Files isn't safe for concurrent
//     multi-instance SQL Server) ---

resource sqlserverApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: sqlServerName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: [
        { name: 'sql-sa-password', value: sqlSaPassword }
      ]
      ingress: {
        external: false
        targetPort: sqlPort
        transport: 'tcp'
      }
    }
    template: {
      containers: [
        {
          name: 'sqlserver'
          image: 'mcr.microsoft.com/mssql/server:2022-latest'
          resources: containerSizes[sqlServerSize]
          env: [
            { name: 'ACCEPT_EULA', value: 'Y' }
            { name: 'MSSQL_PID', value: 'Express' }
            { name: 'MSSQL_SA_PASSWORD', secretRef: 'sql-sa-password' }
            { name: 'MSSQL_TCP_PORT', value: string(sqlPort) }
          ]
          volumeMounts: [
            { volumeName: 'sql-data', mountPath: '/var/opt/mssql' }
          ]
        }
      ]
      volumes: [
        { name: 'sql-data', storageType: 'AzureFile', storageName: sqlDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// --- Redis (internal only, single replica) ---

resource redisApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: redisName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
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
          image: 'redis:7-alpine'
          resources: containerSizes[redisSize]
          command: ['redis-server', '--port', string(redisPort), '--requirepass', redisPassword]
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

// --- Custom domains (optional, per app) ---
//
// Two-phase by necessity, not by choice: Azure can't issue/verify a certificate for a domain until
// DNS proves you control it, and that proof (the TXT record) can only be generated once the
// Container App itself already exists. See fhirbridgeAppCustomDomain's description above for the
// full deploy-twice flow. Left at their default (''), neither of these resources gets created —
// the `if` conditions below make them no-ops — and both apps behave exactly as before this
// feature existed.
//
// NOTE: Microsoft.App/managedEnvironments/managedCertificates is Azure's free managed-certificate
// mechanism for Container Apps custom domains (mirrors azurerm_container_app_environment_managed_
// certificate in the Terraform environment). Validate with `az bicep build` + a real
// `az deployment group validate`/apply before relying on this — this resource type/apiVersion
// combination hasn't been exercised against a live subscription yet in this repo.

resource fhirbridgeAppManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(fhirbridgeAppCustomDomain)) {
  parent: containerAppEnv
  name: '${fhirbridgeAppName}-cert'
  location: location
  properties: {
    subjectName: fhirbridgeAppCustomDomain
    domainControlValidation: 'CNAME'
  }
}

resource demoAppManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(demoAppCustomDomain)) {
  parent: containerAppEnv
  name: '${demoAppName}-cert'
  location: location
  properties: {
    subjectName: demoAppCustomDomain
    domainControlValidation: 'CNAME'
  }
}

// --- FHIRBridge app (Api + Gateway in one image), public ---

resource fhirbridgeApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: fhirbridgeAppName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: concat([
        { name: 'sql-sa-password', value: sqlSaPassword }
        { name: 'jwt-signing-key', value: jwtSigningKey }
      ], registrySecret)
      registries: registryConfig
      ingress: {
        external: true
        targetPort: 80
        transport: 'auto'
        customDomains: !empty(fhirbridgeAppCustomDomain) ? [
          { name: fhirbridgeAppCustomDomain, certificateId: fhirbridgeAppManagedCert.id, bindingType: 'SniEnabled' }
        ] : []
      }
    }
    template: {
      containers: [
        {
          name: 'fhirbridge-app'
          image: '${imageRegistryServer}/fhirbridge-app:${imageTag}'
          resources: containerSizes[fhirbridgeAppSize]
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__FHIRBridgeDb', value: 'Server=${sqlServerName},${sqlPort};Database=FHIRBridge;User Id=sa;Password=${sqlSaPassword};Encrypt=True;TrustServerCertificate=True' }
            { name: 'ConnectionStrings__Redis', value: '${redisName}:${redisPort},password=${redisPassword}' }
            { name: 'Authentication__SigningKey', secretRef: 'jwt-signing-key' }
            { name: 'DataProtection__KeyRingPath', value: '/app/keys' }
            // The demo app is a separate origin whose frontend calls this API cross-origin — its
            // FQDN is predictable from its own name + the environment's default domain, so this
            // needs no second deployment.
            { name: 'Portal__AllowedOrigins__0', value: 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}' }
            { name: 'AllowedHosts', value: '*' }
            { name: 'Swagger__Enabled', value: 'true' }
          ], !empty(demoAppCustomDomain) ? [
            // Only present once demoAppCustomDomain is set — otherwise the demo app only ever
            // calls from its default origin, already covered by __0 above. Without this, binding a
            // custom domain to the demo app would silently break its own calls into this API with
            // a CORS rejection, since its new origin wouldn't be on the allowlist.
            { name: 'Portal__AllowedOrigins__1', value: 'https://${demoAppCustomDomain}' }
          ] : [])
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [sqlserverApp, redisApp]
}

// --- Demo app (self-hosts its own frontend), public ---

resource demoApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: demoAppName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: registrySecret
      registries: registryConfig
      ingress: {
        external: true
        targetPort: 5500
        transport: 'auto'
        customDomains: !empty(demoAppCustomDomain) ? [
          { name: demoAppCustomDomain, certificateId: demoAppManagedCert.id, bindingType: 'SniEnabled' }
        ] : []
      }
    }
    template: {
      containers: [
        {
          name: 'demo-app'
          image: '${imageRegistryServer}/demo-app:${imageTag}'
          resources: containerSizes[demoAppSize]
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__Default', value: 'Server=${sqlServerName},${sqlPort};Database=HealthAppDb;User Id=sa;Password=${sqlSaPassword};Encrypt=True;TrustServerCertificate=True' }
            { name: 'AllowedFrontendOrigin', value: 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [sqlserverApp]
}

// --- Worker (no ingress) ---

resource workerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: workerName
  location: location
  tags: commonTags
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
          image: '${imageRegistryServer}/fhirbridge-worker:${imageTag}'
          resources: containerSizes[workerSize]
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__FHIRBridgeDb', value: 'Server=${sqlServerName},${sqlPort};Database=FHIRBridge;User Id=sa;Password=${sqlSaPassword};Encrypt=True;TrustServerCertificate=True' }
            { name: 'ConnectionStrings__Redis', value: '${redisName}:${redisPort},password=${redisPassword}' }
            { name: 'RuntimeWorker__Enabled', value: 'true' }
            { name: 'Messaging__Provider', value: 'InMemory' }
          ]
        }
      ]
      // fhirbridgeApp is a head start, not a guarantee: both it and worker auto-migrate
      // FHIRBridgeDb on boot and can race on the initial CREATE DATABASE on a fresh database.
      // Container Apps replaces crashed replicas automatically, which turns a lost race into a
      // self-healing retry (see the "migration race" bug writeup in the containerization guide).
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [sqlserverApp, redisApp, fhirbridgeApp]
}

output fhirbridgeAppUrl string = 'https://${fhirbridgeAppName}.${containerAppEnv.properties.defaultDomain}'
output demoAppUrl string = 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}'

// Always populated regardless of whether *CustomDomain is set — add a CNAME (your domain -> the
// matching *Url output's hostname) and a TXT record named asuid.<your domain> with this value at
// your DNS provider, wait for propagation, THEN set fhirbridgeAppCustomDomain/demoAppCustomDomain
// and redeploy.
output fhirbridgeAppDomainVerificationId string = fhirbridgeApp.properties.customDomainVerificationId
output demoAppDomainVerificationId string = demoApp.properties.customDomainVerificationId

output fhirbridgeAppCustomDomainUrl string = !empty(fhirbridgeAppCustomDomain) ? 'https://${fhirbridgeAppCustomDomain}' : ''
output demoAppCustomDomainUrl string = !empty(demoAppCustomDomain) ? 'https://${demoAppCustomDomain}' : ''

// Every resource this deployment created, in a dependency-safe DELETION order (children before
// their parents — e.g. the 5 Container Apps before the environment they run in). Azure keeps this
// output in the deployment's own history (`az deployment group show --name main --query
// properties.outputs.resourceManifest.value`) indefinitely, with no extra resource needed to store
// it — cleanup.sh|ps1 reads this first and deletes exactly these IDs in order, falling back to a
// tag-based scan only if this deployment record isn't found (e.g. deployment history was purged).
output resourceManifest array = [
  sqlserverApp.id
  redisApp.id
  fhirbridgeApp.id
  demoApp.id
  workerApp.id
  sqlDataStorage.id
  redisDataStorage.id
  sqlDataShare.id
  redisDataShare.id
  containerAppEnv.id
  storageAccount.id
  logAnalytics.id
]
