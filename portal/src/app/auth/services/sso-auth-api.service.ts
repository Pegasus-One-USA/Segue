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
 * HIPAA #7: the backend sets the session as HttpOnly cookies directly on these responses (see
 * AuthController.IssueTokenCookiesAndStrip) — `establishSession()` just rebuilds the User from the
 * `profile` field via `buildUserFromProfile` and starts the local session marker. Callers navigate
 * afterwards.
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SessionService } from './session.service';
import { AuthStore } from '../store/auth.store';
import { buildUserFromProfile } from './jwt-user.mapper';
import { LocalLoginResponseDto } from './auth-profile.model';
import { SsoProvider } from './sso.service';

const API = `${environment.apiBase}/api/v1`;

export type LocalLoginResponse = LocalLoginResponseDto;

@Injectable({ providedIn: 'root' })
export class SsoAuthApiService {
  private readonly http    = inject(HttpClient);
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
  acceptInvite(email: string, invitationToken: string, password: string, acceptTerms: boolean): Observable<void> {
    return this.http.post<void>(`${API}/users/accept-invite`, {
      email,
      invitationToken,
      password,
      acceptTerms,
    });
  }

  /** POST /users/accept-invite-sso — accept an invitation using an external IdP identity. */
  acceptInviteViaSso(
    email: string,
    invitationToken: string,
    provider: SsoProvider,
    token: string,
    acceptTerms: boolean,
  ): Observable<LocalLoginResponse> {
    return this.http
      .post<LocalLoginResponse>(`${API}/users/accept-invite-sso`, {
        email,
        invitationToken,
        provider,
        token,
        acceptTerms,
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
   * Runs the exact login success handling: rebuild the user from the profile DTO and start the
   * local session marker. Mirrors AuthService.login / SetupSuperAdmin success paths.
   */
  private establishSession(res: LocalLoginResponse): void {
    const user = buildUserFromProfile(res.profile!);

    this.store.setUser(user);
    this.session.start(user.id, false);
  }
}
