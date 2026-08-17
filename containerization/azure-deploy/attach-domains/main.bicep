// Standalone "custom deployment" form for attaching custom domains to an already-deployed
// main.bicep environment's two public apps (fhirbridge-app, demo-app) -- domain registration only
// (Microsoft.App/containerApps hostname add), no certificate/SSL step. Deliberately minimal: name
// prefix + the two domains, nothing else.
//
// WHY deploymentScripts instead of patching the Container App resources directly: Bicep has no
// azapi_update_resource-style "patch just this one field" mechanism -- redeploying a
// Microsoft.App/containerApps resource from a standalone template generally requires restating its
// ENTIRE configuration (image, secrets, env vars, everything), and getting that wrong risks
// silently wiping the already-running app's config. Wrapping the same
// `az containerapp hostname add` command manage-custom-domain.ps1/.sh already uses (proven safe,
// additive, official Azure CLI behavior) inside a Microsoft.Resources/deploymentScripts resource
// gets a genuine custom-deployment-form experience without that risk.
//
// REQUIRES the deploying principal to have Owner or User Access Administrator rights on the
// resource group (not just Contributor) -- creating the role assignment below for the
// deploymentScript's own managed identity needs Microsoft.Authorization/roleAssignments/write,
// which Contributor alone does not grant. If that's not available, use
// containerization/scripts/manage-custom-domain.ps1|sh directly instead (same underlying
// operation, runs under your own already-authenticated az session, no role assignment needed).
//
// Leave a domain field blank to skip attaching that app's domain this run.

@description('The namePrefix used when main.bicep was originally deployed (e.g. "segue5") -- this is what the target app/environment names are derived from.')
param namePrefix string

@description('Custom domain to attach to the FHIRBridge app (e.g. app.example.com). Leave blank to skip.')
param fhirbridgeAppDomain string = ''

@description('Custom domain to attach to the Demo app (e.g. demo.example.com). Leave blank to skip.')
param demoAppDomain string = ''

@description('Azure region for the deployment script + its managed identity. Defaults to the resource group\'s own region.')
param location string = resourceGroup().location

var fhirbridgeAppName = '${namePrefix}-app'
var demoAppName = '${namePrefix}-demo-app'

resource scriptIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-attach-domains-identity'
  location: location
}

// Contributor -- az containerapp hostname add needs write access to the target Container App.
resource contributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, scriptIdentity.id, 'Contributor')
  scope: resourceGroup()
  properties: {
    principalId: scriptIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  }
}

resource attachDomains 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: '${namePrefix}-attach-domains'
  location: location
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${scriptIdentity.id}': {}
    }
  }
  properties: {
    azCliVersion: '2.60.0'
    // Deletes the container instance + storage that ran this script an hour after it finishes --
    // there's nothing here worth keeping around longer (no state, no output consumed elsewhere).
    retentionInterval: 'PT1H'
    timeout: 'PT15M'
    environmentVariables: [
      { name: 'RESOURCE_GROUP', value: resourceGroup().name }
      { name: 'FHIRBRIDGE_APP_NAME', value: fhirbridgeAppName }
      { name: 'DEMO_APP_NAME', value: demoAppName }
      { name: 'FHIRBRIDGE_DOMAIN', value: fhirbridgeAppDomain }
      { name: 'DEMO_DOMAIN', value: demoAppDomain }
    ]
    scriptContent: '''
      set -e
      if [ -n "$FHIRBRIDGE_DOMAIN" ]; then
        echo "Attaching $FHIRBRIDGE_DOMAIN to $FHIRBRIDGE_APP_NAME..."
        az containerapp hostname add --hostname "$FHIRBRIDGE_DOMAIN" --resource-group "$RESOURCE_GROUP" --name "$FHIRBRIDGE_APP_NAME"
      else
        echo "No FHIRBridge app domain given -- skipping."
      fi
      if [ -n "$DEMO_DOMAIN" ]; then
        echo "Attaching $DEMO_DOMAIN to $DEMO_APP_NAME..."
        az containerapp hostname add --hostname "$DEMO_DOMAIN" --resource-group "$RESOURCE_GROUP" --name "$DEMO_APP_NAME"
      else
        echo "No Demo app domain given -- skipping."
      fi
    '''
  }
  dependsOn: [contributorRoleAssignment]
}

@description('Whatever this deployment script actually printed -- check here first if a domain attach failed (e.g. DNS not resolving yet).')
output scriptLogs string = attachDomains.properties.outputs.?logs ?? 'No output captured -- check the deploymentScripts resource\'s own logs in the Portal (Resource Group -> Deployment Scripts).'
