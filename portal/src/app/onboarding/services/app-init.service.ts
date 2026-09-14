/**
 * AppInitService — first-run bootstrap for the "Create SuperAdmin" setup flow.
 *
 * On app start (via APP_INITIALIZER) `checkSetup()` calls `GET /auth/setup-status`; when the
 * deployment has no users yet the backend returns `{ requiresSetup: true }`. The result is stored
 * in the readonly `requiresSetup` signal, which the setupGuard / default-redirect key off to send
 * an uninitialised deployment to `/setup`. `createSuperAdmin()` POSTs the one-time superadmin and
 * returns the SAME LocalLoginResponse shape the login endpoint returns, so the calling component
 * can reuse the exact login success handling.
 */
import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, tap, map, catchError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthProfileDto } from '../../auth/services/auth-profile.model';

const BASE = `${environment.apiBase}/api/v1/auth`;

export interface SetupStatusResponse {
  requiresSetup: boolean;
  licenseActive: boolean;
  licenseState: string;
}

/** The SMTP settings collected on the same first-run screen — saved enabled alongside the admin account. */
export interface FirstRunEmailSettings {
  host: string;
  port: number;
  enableSsl: boolean;
  username: string | null;
  password: string | null;
  fromAddress: string;
  fromName: string;
}

export interface CreateSuperAdminRequest {
  email: string;
  displayName: string;
  password: string;
  acceptTerms: boolean;
  emailSettings: FirstRunEmailSettings;
  firstName?: string;
  lastName?: string;
}

/**
 * Mirrors the backend LocalLoginResponse (same shape the login endpoint returns). HIPAA #7: the raw
 * token fields are stripped server-side (see AuthController.IssueTokenCookiesAndStrip) — the session
 * is already set as HttpOnly cookies by the time this response arrives.
 */
export interface LocalLoginResponse {
  tokenType: string;
  expiresOnUtc: string;
  requiresPasswordChange: boolean;
  refreshTokenExpiresOnUtc?: string;
  profile?: AuthProfileDto;
}

@Injectable({ providedIn: 'root' })
export class AppInitService {
  private readonly http = inject(HttpClient);

  private readonly _requiresSetup = signal(false);
  /** True when the deployment has no users yet and must run first-run setup. */
  readonly requiresSetup = this._requiresSetup.asReadonly();

  // Backs the header's "license not active" banner (see AppShellComponent) and the server-side gate's
  // portal-side mirror — the API itself is the real enforcement, this just lets the UI explain why an
  // action just got rejected instead of failing silently. Fails open (true/"Active") on any read error
  // so a transient network blip never falsely alarms; the API gate enforces regardless either way.
  private readonly _licenseActive = signal(true);
  readonly licenseActive = this._licenseActive.asReadonly();
  private readonly _licenseState = signal('Active');
  readonly licenseState = this._licenseState.asReadonly();

  /**
   * Queries the backend for setup status. Resilient by design: any failure (API unreachable, etc.)
   * defaults `requiresSetup` to false so the app still boots into the normal login flow.
   */
  checkSetup(): Observable<boolean> {
    return this.http.get<SetupStatusResponse>(`${BASE}/setup-status`).pipe(
      tap(res => {
        this._licenseActive.set(res?.licenseActive ?? true);
        this._licenseState.set(res?.licenseState ?? 'Active');
      }),
      map(res => res?.requiresSetup ?? false),
      tap(requires => this._requiresSetup.set(requires)),
      catchError(() => {
        this._requiresSetup.set(false);
        this._licenseActive.set(true);
        this._licenseState.set('Active');
        return of(false);
      }),
    );
  }

  /**
   * Re-polls the same anonymous status endpoint to refresh just the license-gate signals — called
   * after a login/MFA/magic-link completes (so a freshly-started session reflects the real state) and
   * after an admin applies a new license from Settings > License. Fire-and-forget: a transient failure
   * here just leaves the previous signal values in place rather than surfacing an error anywhere.
   */
  refreshLicenseGate(): void {
    this.http.get<SetupStatusResponse>(`${BASE}/setup-status`).pipe(
      catchError(() => of(null)),
    ).subscribe(res => {
      if (res) {
        this._licenseActive.set(res.licenseActive ?? true);
        this._licenseState.set(res.licenseState ?? 'Active');
      }
    });
  }

  /** Clears the first-run flag so guards stop routing to /setup (e.g. after SSO superadmin setup). */
  markSetupComplete(): void {
    this._requiresSetup.set(false);
  }

  /** POSTs the one-time superadmin. Returns the LocalLoginResponse (login-shaped). */
  createSuperAdmin(req: CreateSuperAdminRequest): Observable<LocalLoginResponse> {
    return this.http.post<LocalLoginResponse>(`${BASE}/setup-superadmin`, req).pipe(
      // Setup is complete once this succeeds — clear the flag so guards stop routing to /setup.
      tap(() => this._requiresSetup.set(false)),
    );
  }
}
