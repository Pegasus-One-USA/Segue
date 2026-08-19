/** Matches the API's SsoConfigurationsDto shape exactly (see SsoConfigurationsDto.cs). SAML, magic-link,
 *  and Entra's login-flow fields (enabled/instance/tenantId/clientId) are live and editable — samlMetadataUrl/
 *  samlAcsUrl are computed/read-only, and googleEnabled is read-only status. */
export interface SsoConfigurationsModel {
  samlEnabled: boolean;
  serviceProviderEntityId: string;
  identityProviderEntityId: string;
  singleSignOnUrl: string;
  identityProviderCertificate: string;
  portalRedirectUrl: string;
  portalErrorRedirectUrl: string;
  magicLinkEnabled: boolean;
  samlMetadataUrl: string;
  samlAcsUrl: string;
  entraEnabled: boolean;
  entraInstance: string;
  entraTenantId: string;
  entraClientId: string;
  googleEnabled: boolean;
}

/** Matches UpdateSsoConfigurationsRequest's expected body shape. */
export interface UpdateSsoConfigurationsRequest {
  samlEnabled: boolean;
  serviceProviderEntityId: string;
  identityProviderEntityId: string;
  singleSignOnUrl: string;
  identityProviderCertificate: string;
  portalRedirectUrl: string;
  portalErrorRedirectUrl: string;
  magicLinkEnabled: boolean;
  entraEnabled: boolean;
  entraInstance: string;
  entraTenantId: string;
  entraClientId: string;
}
