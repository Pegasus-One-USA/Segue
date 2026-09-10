// Standalone module - same reason custom-domain-app-update.bicep exists as its own file: writing
// the route/securityPolicy using values read from those SAME resources (via an `existing`
// reference in the caller, custom-domain.bicep) in one template is a self-reference ARM's validator
// rejects at deploy time. Bicep modules compile to their own isolated nested-deployment scope, so
// this file's route/securityPolicy resources contain no self-reference at all - the caller resolves
// everything it needs from the live resources FIRST, then hands in plain, already-resolved values
// as this module's parameters.

@description('Front Door profile name.')
param profileName string

@description('Front Door endpoint name (parent of the route).')
param endpointName string

@description('Name of the route to update - newCustomDomainId is appended to its customDomains array; everything else passes through unchanged.')
param routeName string

@description('Name of the WAF security policy to update - newCustomDomainId is appended to its domain associations; everything else passes through unchanged.')
param securityPolicyName string

@description('Resource ID of the Front Door custom domain to associate with both the route (so traffic to it actually reaches the origin) and the WAF security policy (so the WAF actually applies to it, not just the default *.azurefd.net domain).')
param newCustomDomainId string

@description('The route\'s current properties, read by the caller via an `existing` reference.')
param existingRouteProperties object

@description('The security policy\'s current properties.parameters object, read by the caller via an `existing` reference.')
param existingSecurityPolicyParameters object

resource profile 'Microsoft.Cdn/profiles@2024-02-01' existing = {
  name: profileName
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' existing = {
  parent: profile
  name: endpointName
}

// Drop any prior entry for this same domain (idempotent re-run) — matches the
// otherCustomDomains/otherAssociationDomains handling pattern custom-domain-app-update.bicep uses
// for the Container App path. main.bicep's original route has no customDomains at all (only
// linkToDefaultDomain), so this reads as an empty array rather than null on a first run here.
var otherRouteCustomDomains = filter(existingRouteProperties.?customDomains ?? [], cd => cd.id != newCustomDomainId)

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: routeName
  properties: union(existingRouteProperties, {
    customDomains: concat(otherRouteCustomDomains, [
      { id: newCustomDomainId }
    ])
  })
}

// main.bicep's original security policy has exactly one association (the default AFD endpoint
// domain) — index [0] is safe on the very first run of this module; a later re-run (e.g. adding a
// second domain) still finds that same single association here and just appends to its domains list.
var existingAssociation = existingSecurityPolicyParameters.associations[0]
var otherAssociationDomains = filter(existingAssociation.domains, d => d.id != newCustomDomainId)

resource securityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: profile
  name: securityPolicyName
  properties: {
    parameters: union(existingSecurityPolicyParameters, {
      associations: [
        union(existingAssociation, {
          domains: concat(otherAssociationDomains, [
            { id: newCustomDomainId }
          ])
        })
      ]
    })
  }
  // Not a hard ordering requirement the way the Container Apps hostname-before-cert flow is — but
  // there's no reason for the WAF association to be created before the route's own domain
  // association exists, so this keeps them in the more sensible order.
  dependsOn: [route]
}
