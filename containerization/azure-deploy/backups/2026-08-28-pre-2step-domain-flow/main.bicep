// FHIRBridge — single-deployment Azure Container Apps template.
//
// This is the "easy install" counterpart to ../terraform/environments/azure: same 7-container
// topology (fhirbridge-app, demo-app, sqlserver, redis, worker, hapi-terminology,
// hapi-terminology-postgres), same Container Apps Environment design, but expressed as one Bicep
// template so it can be deployed with a single command or a single "Deploy to Azure" button click,
// instead of a multi-step `terraform apply`.
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

@description('Password for the hapi_terminology Postgres role backing the HAPI terminology server\'s own schema (internal-only — not the app\'s own FHIRBridgeDb).')
@secure()
param hapiTerminologyPostgresPassword string

@description('Exposes the HAPI terminology server externally (Container Apps external ingress) so it can be reached directly from outside this environment — e.g. a separate terminology admin tool, or a third-party integration — rather than only internally by fhirbridge-app/worker. Defaults to false (internal-only, like sqlserver/redis). Setting hapiTerminologyCustomDomain also forces this on, since Azure Container Apps custom domains require external ingress.')
param hapiTerminologyExternalAccess bool = false

@description('Custom domain for the HAPI terminology server (e.g. terminology.customer.com). Leave blank to use the auto-generated *.azurecontainerapps.io URL once hapiTerminologyExternalAccess is true. Same three-deploy flow as fhirbridgeAppCustomDomain (including the required first deploy with this left blank) — see the hapiTerminologyDomainVerificationId output.')
param hapiTerminologyCustomDomain string = ''

@description('Custom domain for the FHIRBridge app (e.g. app.customer.com). Leave blank to keep the auto-generated *.azurecontainerapps.io URL. REQUIRED three-deploy flow, not two, to avoid InvalidCustomHostNameValidation/RequireCustomHostnameInEnvironment: (0) first deploy with this LEFT BLANK, so the app is actually created — Azure only assigns its customDomainVerificationId once the app exists, and there is no way to know that ID in advance, so setting a domain on the very first-ever deploy of a given namePrefix always fails with "a TXT record ... was not found"; (1) once deployed, read fhirbridgeAppDomainVerificationId, create CNAME (domain -> fhirbridgeAppUrl hostname) + TXT asuid.<domain> = that id, wait for DNS, THEN redeploy with this domain set and bindCustomDomainCertificates=false — registers the hostname (bindingType Disabled, no cert yet); (2) redeploy again with the SAME domain and bindCustomDomainCertificates=true — creates the managed certificate (hostname already exists) then binds SniEnabled SSL.')
param fhirbridgeAppCustomDomain string = ''

@description('Custom domain for the Demo app. Same three-deploy flow as fhirbridgeAppCustomDomain (including the required first deploy with this left blank) — see the demoAppDomainVerificationId output.')
param demoAppCustomDomain string = ''

@description('Phase-2 flag. false (default) = register custom hostnames only (bindingType Disabled), do NOT create managed certificates. true = create managed certificates and bind SniEnabled SSL. Only set true AFTER hostnames were registered in a prior deploy AND DNS CNAME + asuid TXT have propagated. Setting true on first deploy with a new domain causes RequireCustomHostnameInEnvironment.')
param bindCustomDomainCertificates bool = false

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

@description('HAPI terminology server container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param hapiTerminologySize string = 'Large'

@description('HAPI terminology server Postgres container size.')
@allowed(['Small', 'Medium', 'Large', 'XLarge'])
param hapiTerminologyPostgresSize string = 'Medium'

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
var hapiTerminologyPostgresName = '${namePrefix}-term-db' // kept short — Container App names cap at 32 chars
var hapiTerminologyName = '${namePrefix}-term'
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

resource keysDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/keys-data'
  properties: { shareQuota: 1 }
}

resource hapiTerminologyDataShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-01-01' = {
  name: '${storageAccount.name}/default/hapi-terminology-data'
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

resource hapiTerminologyDataStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppEnv
  name: 'hapi-terminology-data'
  properties: {
    azureFile: {
      accountName: storageAccount.name
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: 'hapi-terminology-data'
      accessMode: 'ReadWrite'
    }
  }
  dependsOn: [hapiTerminologyDataShare]
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

// --- HAPI terminology server's Postgres (internal only, single replica) ---

resource hapiTerminologyPostgresApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: hapiTerminologyPostgresName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: [
        { name: 'hapi-terminology-postgres-password', value: hapiTerminologyPostgresPassword }
      ]
      ingress: {
        external: false
        targetPort: 5432
        transport: 'tcp'
      }
    }
    template: {
      containers: [
        {
          name: 'hapi-terminology-postgres'
          image: 'postgres:16-alpine'
          resources: containerSizes[hapiTerminologyPostgresSize]
          env: [
            { name: 'POSTGRES_DB', value: 'hapi_terminology' }
            { name: 'POSTGRES_USER', value: 'hapi_terminology' }
            { name: 'POSTGRES_PASSWORD', secretRef: 'hapi-terminology-postgres-password' }
            // Azure Files (SMB) doesn't support the chown/chmod postgres's entrypoint does on
            // PGDATA at first boot ("Operation not permitted") the way a native/NFS filesystem
            // does - pointing PGDATA at a subdirectory postgres creates and owns itself (rather
            // than the mount root, which is externally provisioned) works around it. SQL Server
            // doesn't hit this because it never tries to chmod its own mount point.
            { name: 'PGDATA', value: '/var/lib/postgresql/data/pgdata' }
          ]
          volumeMounts: [
            { volumeName: 'hapi-terminology-data', mountPath: '/var/lib/postgresql/data' }
          ]
        }
      ]
      volumes: [
        { name: 'hapi-terminology-data', storageType: 'AzureFile', storageName: hapiTerminologyDataStorage.name }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// --- HAPI terminology server (internal only, single replica) ---
//
// Second, dedicated HAPI FHIR instance used only for code-system lookups/validation/expansion/
// translation ($lookup et al.) and the automatic vocabulary syncs in
// FHIRBridge.Infrastructure/Terminology/Hapi — separate from any EHR-sourced FHIR data, which
// never touches this service. Reached only by hostname within the Container Apps environment,
// never externally — see Terminology__BaseUrl on fhirbridgeApp/workerApp below.

resource hapiTerminologyApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: hapiTerminologyName
  location: location
  tags: commonTags
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      secrets: [
        { name: 'hapi-terminology-postgres-password', value: hapiTerminologyPostgresPassword }
      ]
      // external defaults to false (internal-only, like sqlserver/redis) — flipped on by
      // hapiTerminologyExternalAccess, or implicitly by hapiTerminologyCustomDomain (custom
      // domains require external ingress). transport is 'auto' rather than sqlserver/redis' 'tcp'
      // because HAPI serves plain HTTP/REST, both internally and externally.
      ingress: {
        external: hapiTerminologyExternalAccess || !empty(hapiTerminologyCustomDomain)
        targetPort: 8080
        transport: 'auto'
        customDomains: !empty(hapiTerminologyCustomDomain) ? [
          bindCustomDomainCertificates ? {
            name: hapiTerminologyCustomDomain
            certificateId: hapiTerminologyManagedCert.id
            bindingType: 'SniEnabled'
          } : {
            name: hapiTerminologyCustomDomain
            bindingType: 'Disabled'
          }
        ] : []
      }
    }
    template: {
      containers: [
        {
          name: 'hapi-terminology'
          image: 'hapiproject/hapi:latest'
          resources: containerSizes[hapiTerminologySize]
          env: [
            { name: 'SPRING_DATASOURCE_URL', value: 'jdbc:postgresql://${hapiTerminologyPostgresName}:5432/hapi_terminology' }
            { name: 'SPRING_DATASOURCE_USERNAME', value: 'hapi_terminology' }
            { name: 'SPRING_DATASOURCE_PASSWORD', secretRef: 'hapi-terminology-postgres-password' }
            { name: 'SPRING_DATASOURCE_DRIVERCLASSNAME', value: 'org.postgresql.Driver' }
            { name: 'SPRING_JPA_PROPERTIES_HIBERNATE_DIALECT', value: 'ca.uhn.fhir.jpa.model.dialect.HapiFhirPostgres94Dialect' }
            { name: 'HAPI_FHIR_VERSION', value: 'R4' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [hapiTerminologyPostgresApp]
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

resource fhirbridgeAppManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(fhirbridgeAppCustomDomain) && bindCustomDomainCertificates) {
  parent: containerAppEnv
  name: '${fhirbridgeAppName}-cert'
  location: location
  properties: {
    subjectName: fhirbridgeAppCustomDomain
    domainControlValidation: 'CNAME'
  }
}

resource demoAppManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(demoAppCustomDomain) && bindCustomDomainCertificates) {
  parent: containerAppEnv
  name: '${demoAppName}-cert'
  location: location
  properties: {
    subjectName: demoAppCustomDomain
    domainControlValidation: 'CNAME'
  }
}

resource hapiTerminologyManagedCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (!empty(hapiTerminologyCustomDomain) && bindCustomDomainCertificates) {
  parent: containerAppEnv
  name: '${hapiTerminologyName}-cert'
  location: location
  properties: {
    subjectName: hapiTerminologyCustomDomain
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
        // Phase 1: Disabled registers the hostname without a cert (required before managed cert).
        // Phase 2: SniEnabled + certificateId after bindCustomDomainCertificates=true.
        customDomains: !empty(fhirbridgeAppCustomDomain) ? [
          bindCustomDomainCertificates ? {
            name: fhirbridgeAppCustomDomain
            certificateId: fhirbridgeAppManagedCert.id
            bindingType: 'SniEnabled'
          } : {
            name: fhirbridgeAppCustomDomain
            bindingType: 'Disabled'
          }
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
            // Gateway proxies /api to the Api process in this same container (entrypoint binds Api on loopback :5000).
            { name: 'ApiBaseUrl', value: 'http://127.0.0.1:5000/' }
            // Always allow the platform demo FQDN; add custom demo origin when configured.
            { name: 'Portal__AllowedOrigins__0', value: 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}' }
            { name: 'AllowedHosts', value: '*' }
            { name: 'Swagger__Enabled', value: 'true' }
            { name: 'Terminology__BaseUrl', value: 'http://${hapiTerminologyName}:8080/fhir' }
          ], !empty(demoAppCustomDomain) ? [
            { name: 'Portal__AllowedOrigins__1', value: 'https://${demoAppCustomDomain}' }
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
  dependsOn: [sqlserverApp, redisApp, hapiTerminologyApp]
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
          bindCustomDomainCertificates ? {
            name: demoAppCustomDomain
            certificateId: demoAppManagedCert.id
            bindingType: 'SniEnabled'
          } : {
            name: demoAppCustomDomain
            bindingType: 'Disabled'
          }
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
            // Prefer custom demo domain when set so browser origin matches CORS on the API.
            { name: 'AllowedFrontendOrigin', value: !empty(demoAppCustomDomain) ? 'https://${demoAppCustomDomain}' : 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}' }
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
            { name: 'Terminology__BaseUrl', value: 'http://${hapiTerminologyName}:8080/fhir' }
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
  dependsOn: [sqlserverApp, redisApp, hapiTerminologyApp, fhirbridgeApp]
}

output fhirbridgeAppUrl string = 'https://${fhirbridgeAppName}.${containerAppEnv.properties.defaultDomain}'
output demoAppUrl string = 'https://${demoAppName}.${containerAppEnv.properties.defaultDomain}'
output hapiTerminologyUrl string = 'https://${hapiTerminologyName}.${containerAppEnv.properties.defaultDomain}'

// Always populated regardless of whether *CustomDomain is set — add a CNAME (your domain -> the
// matching *Url output's hostname) and a TXT record named asuid.<your domain> with this value at
// your DNS provider. Flow: Phase 1 deploy with domain + bindCustomDomainCertificates=false
// (hostname registered), create DNS, wait; Phase 2 redeploy with bindCustomDomainCertificates=true.
output fhirbridgeAppDomainVerificationId string = fhirbridgeApp.properties.customDomainVerificationId
output demoAppDomainVerificationId string = demoApp.properties.customDomainVerificationId
output hapiTerminologyDomainVerificationId string = hapiTerminologyApp.properties.customDomainVerificationId

output fhirbridgeAppCustomDomainUrl string = !empty(fhirbridgeAppCustomDomain) ? 'https://${fhirbridgeAppCustomDomain}' : ''
output demoAppCustomDomainUrl string = !empty(demoAppCustomDomain) ? 'https://${demoAppCustomDomain}' : ''
output hapiTerminologyCustomDomainUrl string = !empty(hapiTerminologyCustomDomain) ? 'https://${hapiTerminologyCustomDomain}' : ''
output bindCustomDomainCertificates bool = bindCustomDomainCertificates
output customDomainPhase string = empty(fhirbridgeAppCustomDomain) && empty(demoAppCustomDomain) ? 'none' : (bindCustomDomainCertificates ? 'ssl-bound-or-binding' : 'hostname-only-set-dns-then-redeploy-with-bind-true')

// Every resource this deployment created, in a dependency-safe DELETION order (children before
// their parents — e.g. the 5 Container Apps before the environment they run in). Azure keeps this
// output in the deployment's own history (`az deployment group show --name main --query
// properties.outputs.resourceManifest.value`) indefinitely, with no extra resource needed to store
// it — cleanup.sh|ps1 reads this first and deletes exactly these IDs in order, falling back to a
// tag-based scan only if this deployment record isn't found (e.g. deployment history was purged).
output resourceManifest array = [
  sqlserverApp.id
  redisApp.id
  hapiTerminologyPostgresApp.id
  hapiTerminologyApp.id
  fhirbridgeApp.id
  demoApp.id
  workerApp.id
  sqlDataStorage.id
  redisDataStorage.id
  hapiTerminologyDataStorage.id
  keysDataStorage.id
  sqlDataShare.id
  redisDataShare.id
  hapiTerminologyDataShare.id
  keysDataShare.id
  containerAppEnv.id
  storageAccount.id
  logAnalytics.id
]
