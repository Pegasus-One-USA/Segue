/**
 * SsoAuthApiService — the three backend SSO endpoints plus the shared login-success handling.
 *
 * Endpoints (all return the backend LocalLoginResponse — the same shape internal/login returns):
 *   - POST /auth/sso/login              { provider, token }
 *   - POST /users/accept-invite-sso     { email, invitationToken, provider, token }
 *   - POST /auth/setup-superadmin-sso   { provider, token }
 *
 * `provider` is sent as a STRING ("Entra" | "Google") — the API uses JsonStringEnumConverter.
 * `token` is the external IdP ID token (Entra id_token / Google credential).
 *
 * On success, `establishSession()` runs the EXACT login success handling AuthService.login uses:
 * decode the JWT, rebuild the User via buildUserFromJwt, store tokens via SessionService, and
 * populate AuthStore. Callers just navigate afterwards.
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TokenService } from './token.service';
import { SessionService } from './session.service';
import { AuthStore } from '../store/auth.store';
import { buildUserFromJwt } from './jwt-user.mapper';
import { SsoProvider } from './sso.service';

const API = `${environment.apiBase}/api/v1`;

/** Mirrors the backend LocalLoginResponse. */
export interface LocalLoginResponse {
  accessToken: string;
  tokenType: string;
  expiresOnUtc: string;
  requiresPasswordChange: boolean;
  refreshToken?: string;
  refreshTokenExpiresOnUtc?: string;
  profile?: unknown;
}

@Injectable({ providedIn: 'root' })
export class SsoAuthApiService {
  private readonly http    = inject(HttpClient);
  private readonly tokens  = inject(TokenService);
  private readonly session = inject(SessionService);
  private readonly store   = inject(AuthStore);

  /** POST /auth/sso/login — sign in an existing SSO-enabled user. */
  ssoLogin(provider: SsoProvider, token: string): Observable<LocalLoginResponse> {
    return this.http
      .post<LocalLoginResponse>(`${API}/auth/sso/login`, { provider, token })
      .pipe(tap(res => this.establishSession(res)));
  }

  /**
   * POST /users/accept-invite — accept a PASSWORD-based invitation.
   * Returns the created UserDetailDto (NOT a session); the user logs in normally afterwards,
   * so we do not establish a session here. Backend returns 400 for invalid/expired tokens.
   */
  acceptInvite(email: string, invitationToken: string, password: string): Observable<void> {
    return this.http.post<void>(`${API}/users/accept-invite`, {
      email,
      invitationToken,
      password,
    });
  }

  /** POST /users/accept-invite-sso — accept an invitation using an external IdP identity. */
  acceptInviteViaSso(
    email: string,
    invitationToken: string,
    provider: SsoProvider,
    token: string,
  ): Observable<LocalLoginResponse> {
    return this.http
      .post<LocalLoginResponse>(`${API}/users/accept-invite-sso`, {
        email,
        invitationToken,
        provider,
        token,
      })
      .pipe(tap(res => this.establishSession(res)));
  }

  /** POST /auth/setup-superadmin-sso — first-run superadmin via external IdP. */
  setupSuperAdminSso(provider: SsoProvider, token: string): Observable<LocalLoginResponse> {
    return this.http
      .post<LocalLoginResponse>(`${API}/auth/setup-superadmin-sso`, { provider, token })
      .pipe(tap(res => this.establishSession(res)));
  }

  /**
   * Runs the exact login success handling: rebuild the user from the JWT claims, store tokens via
   * SessionService, and populate AuthStore. Mirrors AuthService.login / SetupSuperAdmin success paths.
   */
  private establishSession(res: LocalLoginResponse): void {
    const payload = this.tokens.decodePayload<Record<string, unknown>>(res.accessToken) ?? {};
    const user = buildUserFromJwt(payload);
    user.mustChangePassword = res.requiresPasswordChange ?? false;

    this.store.setUser(user);
    this.session.start(user.id, res.accessToken, res.refreshToken ?? '', false);
  }
}
