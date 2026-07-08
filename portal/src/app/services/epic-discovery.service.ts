import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { SOURCE_DISCOVERY_ENDPOINTS } from '../core/api-endpoints';
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
}

@Injectable({ providedIn: 'root' })
export class EpicDiscoveryService {
  private readonly http = inject(HttpClient);

  /**
   * Live SMART discovery: POSTs the base URL to the backend probe, which fetches the source's public
   * `.well-known/smart-configuration` (OAuth endpoints + scopes) and `/metadata` (supported resource types).
   * Live-only — a failed call surfaces as an error so misconfiguration isn't masked.
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
      })),
    );
  }
}
