/**
 * AuthApiService — real HTTP implementation of IAuthService, wired to the FHIRBridge backend
 * via AUTH_ENDPOINTS (see core/api-endpoints.ts). HIPAA #7: the access/refresh tokens live in
 * HttpOnly cookies the backend sets directly (see AuthController.IssueTokenCookiesAndStrip) —
 * this client never sees the raw JWT. The current user is rebuilt from the `profile` field the
 * backend includes alongside (not from decoding a token), via the shared `buildUserFromProfile`
 * mapper (also used by the SSO login path).
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { map, catchError } from 'rxjs/operators';
import { AUTH_ENDPOINTS } from '../../core/api-endpoints';
import { IAuthService } from './i-auth.service';
import { User, MessageResponse } from '../models/user.model';
import {
  LoginRequest, LoginResponse, LoginResult,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ForgotPasswordResponseDto, ResetPasswordRequest, ChangePasswordRequest,
  MagicLinkRequest, MagicLinkResponseDto, MagicLinkRedeemRequest,
} from '../models/auth-request.model';
import { AuthProfileDto, LocalLoginResponseDto } from './auth-profile.model';
import { buildUserFromProfile } from './jwt-user.mapper';

@Injectable({ providedIn: 'root' })
export class AuthApiService extends IAuthService {
  private readonly http = inject(HttpClient);

  // ─── Login ─────────────────────────────────────────────────────────────────
  // rememberMe used to be silently dropped here — LoginRequest carried it, but this call never put
  // it in the request body, so the backend never saw it regardless of what the checkbox said. This
  // is the actual fix for that: the backend now reads it and decides refresh/CSRF cookie persistence
  // accordingly (see AuthController.IssueTokenCookiesAndStrip) — it never affects authentication itself.
  override login(req: LoginRequest): Observable<LoginResult> {
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.login, {
        email: req.email,
        password: req.password,
        rememberMe: req.rememberMe ?? false,
      })
      .pipe(
        map(dto => this.mapLoginResult(dto)),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Complete an MFA-gated login ────────────────────────────────────────────
  // rememberMe here must be the same choice made on the original login() call — no session/tokens
  // exist yet at that point to store it against, so the caller resends it (see
  // AuthService.completeMfaLogin, which keeps it in the component's own in-memory state across the
  // MFA round-trip).
  override verifyMfaLogin(challengeToken: string, code: string, rememberMe = false): Observable<LoginResponse> {
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.loginMfa, { challengeToken, code, rememberMe })
      .pipe(
        map(dto => this.mapLoginResult(dto) as LoginResponse),
        catchError(err => throwError(() => err)),
      );
  }

  private mapLoginResult(dto: LocalLoginResponseDto): LoginResult {
    if (dto.requiresMfa) {
      return {
        requiresMfa: true,
        mfaChallengeToken: dto.mfaChallengeToken!,
      };
    }

    const user = buildUserFromProfile(dto.profile!);
    return {
      requiresMfa: false,
      user,
    };
  }

  // ─── Logout ────────────────────────────────────────────────────────────────
  override logout(): Observable<void> {
    return this.http.post<void>(AUTH_ENDPOINTS.logout, {}).pipe(
      catchError(() => of(void 0)),
    );
  }

  // ─── Register — no self-registration endpoint exists on the backend ────────
  override register(_req: RegisterRequest): Observable<RegisterResponse> {
    return throwError(() => ({
      code: 'NOT_SUPPORTED',
      message: 'Self-registration is disabled. Ask an administrator to invite you.',
    }));
  }

  // ─── Forgot password ───────────────────────────────────────────────────────
  override forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return this.http.post<ForgotPasswordResponseDto>(AUTH_ENDPOINTS.forgotPassword, { email: req.email }).pipe(
      map(dto => ({
        success: dto?.accepted ?? true,
        message: `If an account exists for ${req.email}, a password-reset link has been sent.`,
      })),
      catchError(err => throwError(() => err)),
    );
  }

  // ─── Reset password ────────────────────────────────────────────────────────
  // Backend looks the account up by email, then verifies the token hash against it — so both
  // fields are required, and the field is named resetToken (not token) on the wire.
  override resetPassword(req: ResetPasswordRequest): Observable<MessageResponse> {
    return this.http
      .post(AUTH_ENDPOINTS.resetPassword, { email: req.email, resetToken: req.token, newPassword: req.newPassword })
      .pipe(
        map(() => ({ success: true, message: 'Password has been reset successfully. You may now sign in.' })),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Change password ───────────────────────────────────────────────────────
  override changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    // The backend re-issues the access-token cookie with an updated pwd_change_required claim on
    // password change — nothing to persist client-side, the browser already has the new cookie.
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.changePassword, {
        currentPassword: req.currentPassword,
        newPassword: req.newPassword,
      })
      .pipe(
        map(() => ({ success: true, message: 'Password changed successfully.' })),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Magic-link request ─────────────────────────────────────────────────────
  override requestMagicLink(req: MagicLinkRequest): Observable<MessageResponse> {
    return this.http.post<MagicLinkResponseDto>(AUTH_ENDPOINTS.magicLinkRequest, { email: req.email }).pipe(
      map(dto => ({
        success: dto?.accepted ?? true,
        message: `If an account exists for ${req.email}, a sign-in link has been sent.`,
      })),
      catchError(err => throwError(() => err)),
    );
  }

  // ─── Magic-link redeem ──────────────────────────────────────────────────────
  override redeemMagicLink(req: MagicLinkRedeemRequest): Observable<LoginResult> {
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.magicLinkRedeem, { email: req.email, token: req.token })
      .pipe(
        map(dto => this.mapLoginResult(dto)),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Get current user ──────────────────────────────────────────────────────
  override getCurrentUser(): Observable<User> {
    return this.http.get<AuthProfileDto>(AUTH_ENDPOINTS.me).pipe(
      map(profile => buildUserFromProfile(profile)),
      catchError(err => throwError(() => err)),
    );
  }

  // ─── Refresh ───────────────────────────────────────────────────────────────
  // The refresh token lives in an HttpOnly cookie the browser sends automatically — nothing to pass here.
  override refreshToken(): Observable<User> {
    return this.http.post<LocalLoginResponseDto>(AUTH_ENDPOINTS.refresh, {}).pipe(
      map(dto => buildUserFromProfile(dto.profile!)),
      catchError(err => throwError(() => err)),
    );
  }
}
