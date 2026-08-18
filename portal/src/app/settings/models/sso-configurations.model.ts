/** Matches the API's SsoConfigurationsDto shape exactly (see SsoConfigurationsDto.cs). SAML/magic-link
 *  fields are live and editable; samlMetadataUrl/samlAcsUrl and entraEnabled/googleEnabled are read-only. */
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
}
