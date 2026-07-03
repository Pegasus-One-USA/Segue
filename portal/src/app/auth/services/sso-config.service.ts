/**
 * SsoConfigService — fetches the deployment's external-IdP configuration once and caches it.
 *
 * Source: `GET ${apiBase}/api/v1/config` →
 *   { entra: { enabled, authority, clientId }, google: { enabled, clientId } }
 *
 * The result drives whether the SSO buttons render at all and provides the client IDs /
 * authority that SsoService needs to bootstrap MSAL + GIS. Fetch is lazy (first `load()` call)
 * and resilient: any failure leaves both providers disabled so nothing breaks when the API is
 * unreachable or SSO isn't configured.
 */
import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

interface SsoConfigResponse {
  entra?: { enabled?: boolean; authority?: string | null; clientId?: string | null };
  google?: { enabled?: boolean; clientId?: string | null };
}

@Injectable({ providedIn: 'root' })
export class SsoConfigService {
  private readonly http = inject(HttpClient);

  private readonly _entraEnabled    = signal(false);
  private readonly _entraAuthority  = signal<string | null>(null);
  private readonly _entraClientId   = signal<string | null>(null);
  private readonly _googleEnabled   = signal(false);
  private readonly _googleClientId  = signal<string | null>(null);
  private readonly _loaded          = signal(false);

  readonly entraEnabled   = this._entraEnabled.asReadonly();
  readonly entraAuthority = this._entraAuthority.asReadonly();
  readonly entraClientId  = this._entraClientId.asReadonly();
  readonly googleEnabled  = this._googleEnabled.asReadonly();
  readonly googleClientId = this._googleClientId.asReadonly();
  readonly loaded         = this._loaded.asReadonly();

  /** True when at least one provider is enabled — components use this to decide whether to show SSO UI. */
  anyEnabled(): boolean {
    return this._entraEnabled() || this._googleEnabled();
  }

  private inflight?: Promise<void>;

  /**
   * Fetches the config once and caches it. Safe to call repeatedly / concurrently — the first
   * call performs the request, subsequent calls await the same promise. Resolves (never rejects)
   * with both providers disabled if the endpoint is unreachable or misconfigured.
   */
  load(): Promise<void> {
    if (this._loaded()) return Promise.resolve();
    if (this.inflight) return this.inflight;

    this.inflight = firstValueFrom(
      this.http.get<SsoConfigResponse>(`${environment.apiBase}/api/v1/config`),
    )
      .then(cfg => {
        const entra  = cfg?.entra  ?? {};
        const google = cfg?.google ?? {};
        // Entra requires an authority + clientId to be usable; gate enabled on their presence.
        const entraUsable = !!entra.enabled && !!entra.authority && !!entra.clientId;
        this._entraEnabled.set(entraUsable);
        this._entraAuthority.set(entra.authority ?? null);
        this._entraClientId.set(entra.clientId ?? null);

        const googleUsable = !!google.enabled && !!google.clientId;
        this._googleEnabled.set(googleUsable);
        this._googleClientId.set(google.clientId ?? null);
      })
      .catch(() => {
        // Resilient default: unreachable / not configured → no SSO.
        this._entraEnabled.set(false);
        this._googleEnabled.set(false);
      })
      .finally(() => {
        this._loaded.set(true);
      });

    return this.inflight;
  }
}
