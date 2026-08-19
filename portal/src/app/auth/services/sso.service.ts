/**
 * SsoService — obtains an external-IdP ID token from Microsoft Entra (via MSAL) or Google (via GIS).
 *
 * The returned `{ provider, token }` is handed to the backend SSO endpoints (see SsoAuthApiService),
 * where `token` is the raw IdP ID token:
 *   - Entra:  the `idToken` from an MSAL loginPopup result.
 *   - Google: the `credential` (JWT) from a GIS credential response.
 *
 * Each provider is initialised lazily and only when SsoConfigService reports it enabled. MSAL is
 * built from the config authority + clientId; GIS is loaded dynamically from Google's CDN.
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
        // A dedicated static page (portal/public/msal-redirect.html), not the SPA's own root — using
        // window.location.origin here raced the app's own auth guard against MSAL's popup-completion
        // detection: the popup would land back on the bootstrapping Angular app, get redirected to
        // /auth/login by the guard (no session yet), and get stuck there instead of MSAL closing it.
        redirectUri: `${window.location.origin}/msal-redirect.html`,
      },
      cache: {
        cacheLocation: 'sessionStorage',
      },
    });
    await instance.initialize();
    this.msalInstance = instance;
    return instance;
  }

  async signInWithEntra(): Promise<SsoResult> {
    const instance = await this.getMsal();
    const result: AuthenticationResult = await instance.loginPopup({
      scopes: ['openid', 'email', 'profile'],
      prompt: 'select_account',
    });
    if (!result?.idToken) {
      throw new Error('Microsoft sign-in did not return an identity token.');
    }
    return { provider: 'Entra', token: result.idToken };
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
