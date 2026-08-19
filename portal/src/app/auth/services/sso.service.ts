/**
 * SsoService — obtains an external-IdP ID token from Microsoft Entra (via MSAL) or Google (via GIS).
 *
 * The returned `{ provider, token }` is handed to the backend SSO endpoints (see SsoAuthApiService),
 * where `token` is the raw IdP ID token:
 *   - Entra:  the `idToken` from MSAL's redirect response.
 *   - Google: the `credential` (JWT) from a GIS credential response.
 *
 * Each provider is initialised lazily and only when SsoConfigService reports it enabled. MSAL is
 * built from the config authority + clientId; GIS is loaded dynamically from Google's CDN.
 *
 * Entra uses `loginRedirect`, not `loginPopup`: a popup relies on the *opener* window detecting
 * when the popup navigates back to the redirect URI, via a direct `window.opener`/window-handle
 * reference. Real-world testing found `window.opener` comes back `null` once the popup leaves for
 * Microsoft's cross-origin login page — the browser severs that reference (a widely-reported MSAL
 * popup limitation, not something this app's own headers cause) — leaving the popup stuck on the
 * redirect page forever with no error surfaced anywhere. `loginRedirect` sidesteps the whole
 * problem: the single tab navigates to Microsoft and back, no cross-window reference needed at
 * all. `handleRedirectResponse()` is called once on every app boot (via APP_INITIALIZER, see
 * app.config.ts) to pick up the response after that return trip.
 */
import { Injectable, inject } from '@angular/core';
import {
  PublicClientApplication,
  type IPublicClientApplication,
  type AuthenticationResult,
} from '@azure/msal-browser';
import { SsoConfigService } from './sso-config.service';

export type SsoProvider = 'Entra' | 'Google';
export interface SsoResult {
  provider: SsoProvider;
  token: string;
}

const GIS_SRC = 'https://accounts.google.com/gsi/client';

@Injectable({ providedIn: 'root' })
export class SsoService {
  private readonly config = inject(SsoConfigService);

  private msalInstance?: IPublicClientApplication;
  private gisLoaded?: Promise<void>;
  private gisInitialized = false;

  // ─── Microsoft Entra (MSAL) ───────────────────────────────────────────────
  private async getMsal(): Promise<IPublicClientApplication> {
    await this.config.load();
    if (!this.config.entraEnabled()) {
      throw new Error('Microsoft sign-in is not enabled for this deployment.');
    }
    if (this.msalInstance) return this.msalInstance;

    const authority = this.config.entraAuthority();
    const clientId  = this.config.entraClientId();
    if (!authority || !clientId) {
      throw new Error('Microsoft sign-in is not configured.');
    }

    const instance = new PublicClientApplication({
      auth: {
        clientId,
        authority,
        // The app's own root — for redirect flow (unlike the popup flow this replaced) the browser
        // tab itself lands here after Microsoft authenticates, and handleRedirectResponse() (called
        // from app.config.ts's APP_INITIALIZER, before the router's own initial navigation runs)
        // picks up the response.
        redirectUri: window.location.origin,
      },
      cache: {
        cacheLocation: 'sessionStorage',
      },
    });
    await instance.initialize();
    this.msalInstance = instance;
    return instance;
  }

  /** Navigates the browser to Microsoft's sign-in page. Does not return a result directly — the
   *  page navigates away, and the response is picked up by handleRedirectResponse() on the next
   *  app boot, after the browser returns to redirectUri. */
  async signInWithEntra(): Promise<void> {
    const instance = await this.getMsal();
    await instance.loginRedirect({
      scopes: ['openid', 'email', 'profile'],
      prompt: 'select_account',
    });
  }

  /**
   * Call once on every app boot (see app.config.ts). Resolves to a result only when this boot is
   * the browser returning from an Entra loginRedirect; resolves to `null` on a normal boot, or if
   * Entra isn't enabled/configured — never throws, so it's safe to call unconditionally at startup.
   */
  async handleRedirectResponse(): Promise<SsoResult | null> {
    try {
      const instance = await this.getMsal();
      const result: AuthenticationResult | null = await instance.handleRedirectPromise();
      if (!result?.idToken) return null;
      return { provider: 'Entra', token: result.idToken };
    } catch {
      return null;
    }
  }

  // ─── Google Identity Services (GIS) ───────────────────────────────────────
  private loadGisScript(): Promise<void> {
    if (this.gisLoaded) return this.gisLoaded;

    this.gisLoaded = new Promise<void>((resolve, reject) => {
      if (typeof window !== 'undefined' && window.google?.accounts?.id) {
        resolve();
        return;
      }
      const existing = document.querySelector<HTMLScriptElement>(`script[src="${GIS_SRC}"]`);
      if (existing) {
        existing.addEventListener('load', () => resolve());
        existing.addEventListener('error', () => reject(new Error('Failed to load Google sign-in.')));
        return;
      }
      const script = document.createElement('script');
      script.src = GIS_SRC;
      script.async = true;
      script.defer = true;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error('Failed to load Google sign-in.'));
      document.head.appendChild(script);
    });

    return this.gisLoaded;
  }

  async signInWithGoogle(): Promise<SsoResult> {
    await this.config.load();
    if (!this.config.googleEnabled()) {
      throw new Error('Google sign-in is not enabled for this deployment.');
    }
    const clientId = this.config.googleClientId();
    if (!clientId) {
      throw new Error('Google sign-in is not configured.');
    }

    await this.loadGisScript();

    return new Promise<SsoResult>((resolve, reject) => {
      try {
        google.accounts.id.initialize({
          client_id: clientId,
          callback: (response) => {
            if (response?.credential) {
              resolve({ provider: 'Google', token: response.credential });
            } else {
              reject(new Error('Google sign-in did not return a credential.'));
            }
          },
          cancel_on_tap_outside: true,
          use_fedcm_for_prompt: true,
        });
        this.gisInitialized = true;

        google.accounts.id.prompt((notification) => {
          // If the One Tap / prompt cannot be shown (dismissed, suppressed, no session),
          // surface a clear error rather than hanging forever.
          if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
            reject(new Error('Google sign-in was dismissed or could not be displayed.'));
          }
        });
      } catch (err) {
        reject(err instanceof Error ? err : new Error('Google sign-in failed.'));
      }
    });
  }
}
