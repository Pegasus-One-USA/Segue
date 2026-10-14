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
  ping?: PingConfigurationModel;
}

/** One row of the ordered mapping (list order = priority). A Segue user holds a single role. */
export interface PingGroupRoleMapping {
  group: string;
  role: string;
}

export type PingRoleSyncMode = 'AutoSwitch' | 'AllowDifferent' | 'RestrictMismatch';
export type PingConflictMode = 'Deny' | 'UseMappingOrder';
export type PingNoMappedGroupMode = 'Deny' | 'AllowSegueRole';

/** PingOne settings as returned by the API. The worker secret is write-only: only whether one is set comes back. */
export interface PingConfigurationModel {
  enabled: boolean;
  environmentId: string;
  authDomain: string;
  apiDomain: string;
  clientId: string;
  workerClientId: string;
  workerSecretConfigured: boolean;
  /** e.g. "64 characters, ending …3TPx" — compare with the PingOne console to spot a mis-paste. */
  workerSecretHint?: string | null;
  jitProvisioning: boolean;
  applyToSaml: boolean;
  groupRoleMappings: PingGroupRoleMapping[];
  roleSyncMode: PingRoleSyncMode;
  conflictMode: PingConflictMode;
  noMappedGroupMode: PingNoMappedGroupMode;
}

export interface UpdatePingConfigurationRequest {
  enabled: boolean;
  environmentId: string;
  authDomain: string;
  apiDomain: string;
  clientId: string;
  workerClientId: string;
  /** Blank keeps the stored secret; a value replaces it. */
  workerClientSecret: string | null;
  jitProvisioning: boolean;
  applyToSaml: boolean;
  groupRoleMappings: PingGroupRoleMapping[];
  roleSyncMode: PingRoleSyncMode;
  conflictMode: PingConflictMode;
  noMappedGroupMode: PingNoMappedGroupMode;
}

export interface ConnectionTestResult {
  success: boolean;
  message: string;
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
  ping?: UpdatePingConfigurationRequest;
}
