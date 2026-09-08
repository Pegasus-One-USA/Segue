import { EhrVendor } from '../../ehr-endpoints/models/ehr-endpoint.model';

/** Mirrors the backend's AuthenticationType enum (serialized as a string). */
export type AuthenticationTypeModel = 'None' | 'SmartBackendServices' | 'OAuthClientCredentials' | 'ApiKey';

/** Matches SourceAuthenticationDto.cs exactly, so no DTO<->model mapping is needed. */
export interface SourceAuthenticationModel {
  authenticationType: AuthenticationTypeModel;
  clientId?: string | null;
  tokenEndpoint?: string | null;
  /** The SMART authorization (browser redirect) endpoint resolved by the wizard's "Discover". Persisted so
   *  re-opening a saved connection shows back the URL that was actually configured, rather than a guessed
   *  per-vendor default. The interactive sign-in flow itself still re-discovers this live at authorize time,
   *  so a stale value can never redirect a user to the wrong authorization server. Null for Backend System
   *  (client_credentials) connections, which never use an authorization endpoint. */
  authorizationEndpoint?: string | null;
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
  /** The scopes Epic (or another EHR) actually granted the app, from the last successful "Discover" token
   *  exchange (backend-auth-scopes probe). Null until Discover has run once; purely informational — distinct
   *  from `scopes`, which is what FHIRBridge requests. */
  discoveredScopes?: string[] | null;
  /** athenahealth only — the bare numeric practice id (e.g. "195900") the backend builds the
   *  ah-practice=Organization/a-1.Practice-{id} reference from. Null for every other vendor. */
  practiceId?: string | null;
  /** Write-only: a wizard-typed raw client secret to provision at (clientSecretKeyVaultName, clientSecretName)
   *  when saving — mirrors CreateDestinationConfigurationRequest.inlineSecret. Never populated on a GET
   *  response (the backend never returns raw secret values); null on save leaves the existing stored secret
   *  (if any) untouched. */
  inlineClientSecret?: string | null;
  /** Where OAuth2ClientCredentialsTokenProvider places client id/secret on the token request — "post" (form
   *  body, the default) or "basic" (Authorization header). Some client-credentials authorization servers (e.g.
   *  Okta-fronted ones) reject client_secret_post with invalid_client and require Basic instead. Null behaves
   *  as "post". Only meaningful for Client Secret auth. */
  authPlacement?: 'post' | 'basic' | null;
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
  lastSuccessfulSyncUtcByResourceType?: Record<string, string> | null;
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
  createdOnUtc?: string | null;
  createdBy?: string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?: string | null;
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

/** Matches the backend's ApplicationType enum (serialized as a string) — the "Audience" column/filter. */
export type ApplicationTypeModel = 'Backend' | 'EhrLaunch' | 'Standalone' | 'Patient';

export type SourceSortColumn = 'name' | 'sourceSystemType' | 'applicationType' | 'isEnabled' | 'actionOn';
export type SortOrder = 'asc' | 'desc';

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface SourceConnectionFilter {
  search?: string;
  sourceSystemType?: EhrVendor;
  applicationType?: ApplicationTypeModel;
  isEnabled?: boolean;
  sortBy?: SourceSortColumn;
  sortOrder?: SortOrder;
  page: number;
  pageSize: number;
}
