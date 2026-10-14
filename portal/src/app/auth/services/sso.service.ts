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

export type SsoProvider = 'Entra' | 'Google' | 'Ping';
export interface SsoResult {
  provider: SsoProvider;
  token: string;
}

const GIS_SRC = 'https://accounts.google.com/gsi/client';
const PING_PKCE_KEY = 'segue.ping.pkce';

interface PingPkceState {
  state: string;
  nonce: string;
  verifier: string;
}

function base64Url(bytes: Uint8Array): string {
  let bin = '';
  bytes.forEach(b => (bin += String.fromCharCode(b)));
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function randomString(byteLength: number): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

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
    // PingOne returns with ?code=...&state=... (authorization-code + PKCE); Entra's MSAL response is a
    // URL fragment. A pending PKCE record in sessionStorage is what marks a boot as a Ping return.
    const ping = await this.handlePingRedirect();
    if (ping) return ping;

    try {
      const instance = await this.getMsal();
      const result: AuthenticationResult | null = await instance.handleRedirectPromise();
      if (!result?.idToken) return null;
      return { provider: 'Entra', token: result.idToken };
    } catch {
      return null;
    }
  }

  // ─── PingOne (OIDC authorization code + PKCE, public client) ──────────────
  /** Navigates the tab to PingOne's sign-in page. The response is picked up on the next app boot by
   *  handlePingRedirect() (via handleRedirectResponse()), same shape as the Entra redirect flow. */
  async signInWithPing(): Promise<void> {
    await this.config.load();
    if (!this.config.pingEnabled()) {
      throw new Error('Ping sign-in is not enabled for this deployment.');
    }
    const authority = this.config.pingAuthority();
    const clientId  = this.config.pingClientId();
    if (!authority || !clientId) {
      throw new Error('Ping sign-in is not configured.');
    }

    const pkce: PingPkceState = {
      state: randomString(16),
      nonce: randomString(16),
      verifier: randomString(48),
    };
    const challenge = base64Url(new Uint8Array(
      await crypto.subtle.digest('SHA-256', new TextEncoder().encode(pkce.verifier)),
    ));
    sessionStorage.setItem(PING_PKCE_KEY, JSON.stringify(pkce));

    const params = new URLSearchParams({
      response_type: 'code',
      client_id: clientId,
      redirect_uri: window.location.origin,
      scope: 'openid email profile',
      state: pkce.state,
      nonce: pkce.nonce,
      code_challenge: challenge,
      code_challenge_method: 'S256',
    });
    window.location.assign(`${authority}/authorize?${params.toString()}`);
  }

  /** Resolves to a result only when this boot is the return from PingOne; otherwise null. Never throws. */
  private async handlePingRedirect(): Promise<SsoResult | null> {
    const raw = sessionStorage.getItem(PING_PKCE_KEY);
    if (!raw) return null;

    const query = new URLSearchParams(window.location.search);
    const code  = query.get('code');
    const state = query.get('state');
    if (!code && !query.get('error')) return null;

    // One-shot: consume the PKCE record and strip the response from the address bar either way.
    sessionStorage.removeItem(PING_PKCE_KEY);
    window.history.replaceState({}, document.title, window.location.pathname);

    try {
      const pkce = JSON.parse(raw) as PingPkceState;
      if (!code || state !== pkce.state) return null;

      await this.config.load();
      const authority = this.config.pingAuthority();
      const clientId  = this.config.pingClientId();
      if (!authority || !clientId) return null;

      const response = await fetch(`${authority}/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'authorization_code',
          code,
          redirect_uri: window.location.origin,
          client_id: clientId,
          code_verifier: pkce.verifier,
        }),
      });
      if (!response.ok) return null;

      const idToken = (await response.json())?.id_token as string | undefined;
      if (!idToken) return null;

      // The API validates signature/issuer/audience; the nonce is only checkable here, so bind it to this tab.
      const payload = JSON.parse(atob(idToken.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
      if (payload?.nonce !== pkce.nonce) return null;

      return { provider: 'Ping', token: idToken };
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
