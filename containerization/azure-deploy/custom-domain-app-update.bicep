// Standalone module - the only reason this write lives in its own file (rather than as a resource
// declared directly in custom-domain.bicep) is to break a genuine ARM circular dependency: writing
// containerApps/{appName} using values read from that SAME resource (via an `existing` reference
// plus reference()/listSecrets() calls) in one template is a self-reference ARM's validator
// rejects at deploy time (`Circular dependency detected on resource: .../containerApps/{name}`).
// Bicep modules compile to their own isolated nested-deployment scope, so this file's containerApps
// resource contains no self-reference at all - the caller (custom-domain.bicep) resolves
// everything it needs from the live app FIRST, then hands in plain, already-resolved values as this
// module's parameters.
//
// The managed certificate ALSO lives in this module, not the caller, for a second, unrelated Bicep
// restriction: a resource's `if` condition (like the certificate's `if (bindCertificate)`) can only
// reference plain parameters/variables, never another resource's live properties (Bicep BCP177) -
// so `bindCertificate` can't be computed via an `if` in the parent template directly from the app's
// current customDomains. The parent instead computes it inline as an ordinary (already-resolved)
// boolean PARAMETER passed in here, where a plain-parameter `if` condition is fully allowed.

@description('Name of the container app being updated.')
param appName string

@description('Azure region - must match the app\'s existing region.')
param location string

@description('Name of the Container Apps managed environment (parents the managed certificate).')
param environmentName string

@description('Resource ID of the Container Apps managed environment this app belongs to.')
param managedEnvironmentId string

@description('The domain being registered/bound on this app.')
param domain string

@description('Whether `domain` was already present in the app\'s customDomains BEFORE this deployment (computed by the caller from live state) — true creates the managed certificate and binds SniEnabled SSL now; false just registers the hostname (bindingType Disabled) for a subsequent run to bind once DNS has propagated.')
param bindCertificate bool

@description('The app\'s current full configuration object (secrets redacted, ingress as-is) — read by the caller via an `existing` reference. `secrets` and `ingress.customDomains` are overridden below; everything else (registries, targetPort, transport, existing traffic weights, etc.) passes through unchanged.')
param baseConfiguration object

@description('The app\'s current secrets WITH real values (from the caller\'s listSecrets() call — a plain GET/`existing` reference redacts secret values to name-only, which would blank them out if reused as-is).')
param existingSecrets array

@description('Every custom domain already on the app OTHER than `domain` (already filtered by the caller) — preserved untouched so this app\'s other bound domains survive this update.')
param otherCustomDomains array

@description('The app\'s existing containers/scale template, passed through unchanged.')
param containerTemplate object

resource env 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource cert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (bindCertificate) {
  parent: env
  name: '${appName}-${replace(domain, '.', '-')}-cert'
  location: location
  properties: {
    subjectName: domain
    domainControlValidation: 'CNAME'
  }
}

var thisCustomDomain = bindCertificate ? {
  name: domain
  certificateId: cert.id
  bindingType: 'SniEnabled'
} : {
  name: domain
  bindingType: 'Disabled'
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  // `identity` is a resource-level property, a sibling of `properties` below - NOT part of
  // `properties.template`/`properties.configuration`, so passing those through unchanged (as this
  // module already does) does NOT preserve it. Omitting this block entirely, as an earlier version
  // of this module did, silently resets identity to `None` on every domain-binding redeploy -
  // confirmed live: segueApp/worker authenticate to the tenant secrets Key Vault via this
  // system-assigned identity (see main.bicep), and losing it here made that authentication hang
  // indefinitely with zero log output, well before Kestrel even started listening - the app looked
  // "stuck," not crashed, and only after a custom domain had just been bound. Matches main.bicep's
  // identical block unconditionally, the same way, for the same reason.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: union(baseConfiguration, {
      secrets: existingSecrets
      // Custom domains require external ingress — forced on unconditionally here (the app this
      // module runs against is already external by default in main.bicep, but this keeps the
      // module correct even if that changes). Every other ingress property (targetPort, transport,
      // existing traffic weights) is preserved via union.
      ingress: union(baseConfiguration.ingress, {
        external: true
        customDomains: concat(otherCustomDomains, [thisCustomDomain])
      })
    })
    template: containerTemplate
  }
}
