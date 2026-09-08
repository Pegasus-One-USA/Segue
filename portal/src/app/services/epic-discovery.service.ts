import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { SOURCE_CAPABILITIES_ENDPOINTS, SOURCE_DISCOVERY_ENDPOINTS } from '../core/api-endpoints';
import { EnvKey } from '../models/epic-env.model';

export interface DiscoveredEndpoints {
  token: string;
  authorize: string;
  scopesSupported: string[];
  resourceTypes: string[];
  codeChallengeMethods: string[];
  capabilities: string[];
  tokenEndpointAuthMethods: string[];
  resourceTypesError: string | null;
  /** Set when the endpoint has no /.well-known/smart-configuration at all (plain, non-SMART FHIR R4 server) —
   *  token/authorize above are then empty and must be filled in manually. */
  smartConfigurationError: string | null;
}

interface ProbeSmartConfiguration {
  authorizationEndpoint: string | null;
  tokenEndpoint: string | null;
  scopesSupported: string[];
  codeChallengeMethodsSupported: string[];
  capabilities: string[];
  tokenEndpointAuthMethodsSupported: string[];
}

interface ProbeResponse {
  smartConfiguration: ProbeSmartConfiguration;
  resourceTypes: string[];
  resourceTypesError: string | null;
  smartConfigurationError: string | null;
}

export interface BackendAuthScopesRequest {
  tokenEndpoint: string;
  clientId: string;
  /** Which client-credentials method to exchange with — must match whichever the caller's form has selected. */
  authMethod: 'secret' | 'jwt';
  scope: string;
  // ── jwt only ──
  keyId?: string | null;
  privateKeyVaultName?: string;
  privateKeySecretName?: string;
  // ── secret only ──
  clientSecret?: string;
  /** Where to place client id/secret — 'post' (form body, the default) or 'basic' (Authorization header). */
  authPlacement?: 'post' | 'basic' | null;
}

export interface BackendAuthScopesResult {
  success: boolean;
  grantedScopes: string[];
  error: string | null;
}

/** Server-generated scope set for a source — the backend's GeneratedScopesDto. `scopeString` is what the
 *  pipeline will actually request, so Test Connection exchanges for exactly that. `unsupportedScopes` are the
 *  ones the source's own scopes_supported doesn't advertise: reported rather than requested, because eCW and
 *  athenahealth both fail the WHOLE token request on one unrecognized scope. */
export interface DerivedScopesResult {
  scopeVersion: string;
  scopeVersionDetected: boolean;
  scopes: string[];
  scopeString: string;
  unsupportedScopes: string[];
  validatedAgainstDiscovery: boolean;
}

@Injectable({ providedIn: 'root' })
export class EpicDiscoveryService {
  private readonly http = inject(HttpClient);

  /**
   * Live SMART discovery: POSTs the base URL to the backend probe, which fetches the source's public
   * `.well-known/smart-configuration` (OAuth endpoints + scopes) and `/metadata` (supported resource types).
   * Both are best-effort server-side — a plain (non-SMART) FHIR R4 server has no smart-configuration document at
   * all, which surfaces here as `smartConfigurationError` rather than failing the whole call, so the wizard can
   * fall back to manual token/authorize entry instead of blocking the rest of the form.
   */
  discover(baseUrl: string, _envKey?: EnvKey): Observable<DiscoveredEndpoints> {
    return this.http.post<ProbeResponse>(SOURCE_DISCOVERY_ENDPOINTS.probe, { baseUrl }).pipe(
      map(response => ({
        token: response.smartConfiguration.tokenEndpoint ?? '',
        authorize: response.smartConfiguration.authorizationEndpoint ?? '',
        scopesSupported: response.smartConfiguration.scopesSupported ?? [],
        resourceTypes: response.resourceTypes ?? [],
        codeChallengeMethods: response.smartConfiguration.codeChallengeMethodsSupported ?? [],
        capabilities: response.smartConfiguration.capabilities ?? [],
        tokenEndpointAuthMethods: response.smartConfiguration.tokenEndpointAuthMethodsSupported ?? [],
        resourceTypesError: response.resourceTypesError ?? null,
        smartConfigurationError: response.smartConfigurationError ?? null,
      })),
    );
  }

  /**
   * Backend System only: runs a real client_credentials exchange against the source's token endpoint — private_key_jwt
   * (using a signing key already provisioned into the secret store) or a plain client secret, per `request.authMethod`
   * — and returns the scopes the source actually granted the app — never the access token itself.
   */
  testBackendAuthScopes(request: BackendAuthScopesRequest): Observable<BackendAuthScopesResult> {
    return this.http.post<BackendAuthScopesResult>(SOURCE_DISCOVERY_ENDPOINTS.backendAuthScopes, request);
  }

  /**
   * The scope set a SAVED connection will actually request, generated server-side from its application type,
   * vendor profile and the resource types passed here, then validated against the source's live
   * `scopes_supported`. Requires a persisted connection id — a brand-new connection has none yet, so callers
   * fall back to the vendor's own wildcard (see resolveTestConnectionScope in the vendor source forms).
   *
   * `scopeVersion` is deliberately omitted by callers: the server detects it from the discovery document, which
   * is the only way athenahealth's and eCW's hard v1-only requirement gets honoured (both reject a v2 `.rs`
   * resource scope outright — see EpicSourceConnectionScopeSyncService).
   */
  derivedScopes(
    sourceConnectionId: string,
    resources: string[],
    scopeVersion?: 'v1' | 'v2',
  ): Observable<DerivedScopesResult> {
    let params = new HttpParams();
    if (resources.length) {
      params = params.set('resources', resources.join(','));
    }
    if (scopeVersion) {
      params = params.set('scopeVersion', scopeVersion);
    }
    return this.http.get<DerivedScopesResult>(
      SOURCE_CAPABILITIES_ENDPOINTS.derivedConfig(sourceConnectionId),
      { params },
    );
  }
}
