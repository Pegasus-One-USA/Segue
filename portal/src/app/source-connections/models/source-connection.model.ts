import { EhrVendor } from '../../ehr-endpoints/models/ehr-endpoint.model';

/** Mirrors the backend's AuthenticationType enum (serialized as a string). */
export type AuthenticationTypeModel = 'None' | 'SmartBackendServices' | 'OAuthClientCredentials' | 'ApiKey';

/** Matches SourceAuthenticationDto.cs exactly, so no DTO<->model mapping is needed. */
export interface SourceAuthenticationModel {
  authenticationType: AuthenticationTypeModel;
  clientId?: string | null;
  tokenEndpoint?: string | null;
  scopes: string[];
  clientSecretKeyVaultName?: string | null;
  clientSecretName?: string | null;
  privateKeyKeyVaultName?: string | null;
  privateKeySecretName?: string | null;
  keyId?: string | null;
  /** The URL actually registered with the EHR to fetch this connection's JWK Set — FHIRBridge's own hosted
   *  .well-known/jwks.json for a Generated/Imported key, or an admin-typed external URL for a key served
   *  elsewhere. Purely informational (FHIRBridge never fetches it itself); persisted so reopening this
   *  connection shows back whatever was actually registered instead of only ever guessing. */
  jwksUrl?: string | null;
}

/** Matches SourceInteractiveConfigurationDto.cs exactly. */
export interface SourceInteractiveConfigurationModel {
  redirectUris: string[];
  launchUrl?: string | null;
  trustedIssuers: string[];
  patientSelectionMethod?: string | null;
  postLaunchRedirectUri?: string | null;
  launchDisplayMode?: string | null;
}

/** Matches SourceRetrievalConfigurationDto.cs exactly. */
export interface SourceRetrievalConfigurationModel {
  retrievalMethod: string;
  resourceTypes: string[];
  searchCriteria?: string | null;
  incrementalSyncEnabled: boolean;
  pageSize?: number | null;
  sortOrder?: string | null;
  includeParameters?: string[] | null;
  revIncludeParameters?: string[] | null;
  retryPolicy?: string | null;
  timeoutSeconds?: number | null;
  maxRecordsPerRun?: number | null;
  lastSuccessfulSyncUtc?: string | null;
  exportScope?: string | null;
  groupId?: string | null;
  patientIds?: string[] | null;
  outputFormat?: string | null;
}

/** Matches the API's SourceConnectionDto shape exactly (see SourceConnectionDto.cs). */
export interface SourceConnectionModel {
  id: string;
  name: string;
  sourceSystemType: EhrVendor;
  baseUrl: string;
  authentication: SourceAuthenticationModel;
  isEnabled: boolean;
  applicationType?: string | null;
  interactive?: SourceInteractiveConfigurationModel | null;
  retrieval?: SourceRetrievalConfigurationModel | null;
}

/** Matches CreateSourceConnectionRequest's expected body shape for both create (POST) and update (PUT). */
export interface SourceConnectionRequest {
  name: string;
  sourceSystemType: EhrVendor;
  baseUrl: string;
  authentication: SourceAuthenticationModel;
  applicationType?: string | null;
  interactive?: SourceInteractiveConfigurationModel | null;
  retrieval?: SourceRetrievalConfigurationModel | null;
}

/** Matches the API's GeneratedSigningKeyDto shape exactly (see GeneratedSigningKeyDto.cs). The private key itself
 *  is never returned — only what's needed to wire it into SourceAuthenticationModel on save. */
export interface GeneratedSigningKeyModel {
  keyId: string;
  keyVaultName: string;
  secretName: string;
  algorithm: string;
}
