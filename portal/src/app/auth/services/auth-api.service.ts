/**
 * AuthApiService — real HTTP implementation of IAuthService, wired to the FHIRBridge backend
 * via AUTH_ENDPOINTS (see core/api-endpoints.ts). The access token is a JWT carrying `roles` +
 * `permissions` claims; the current user is rebuilt from those claims through the single shared
 * `buildUserFromJwt` mapper (also used by the SSO login path). Wired in app.config.ts.
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { map, catchError, tap } from 'rxjs/operators';
import { AUTH_ENDPOINTS } from '../../core/api-endpoints';
import { IAuthService } from './i-auth.service';
import { TokenService } from './token.service';
import { User, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse, LoginResult,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ForgotPasswordResponseDto, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';
import { buildUserFromJwt } from './jwt-user.mapper';

// ─── Backend DTO shapes (camelCase over the wire) ──────────────────────────────
interface AuthProfileDto {
  userId:         string;
  externalUserId: string;
  email:          string | null;
  displayName:    string | null;
  claimRoles:     string[];
}

interface LocalLoginResponseDto {
  requiresMfa:               boolean;
  mfaChallengeToken?:        string | null;
  accessToken:               string | null;
  tokenType:                string | null;
  expiresOnUtc:             string | null;
  requiresPasswordChange:   boolean;
  profile?:                 AuthProfileDto | null;
  refreshToken?:            string | null;
  refreshTokenExpiresOnUtc?: string | null;
}

// Backend returns an absolute expiry timestamp; LoginResponse/TokenPair need relative seconds.
function secondsUntil(isoUtc: string | undefined): number {
  if (!isoUtc) return 3600;
  return Math.max(60, Math.round((new Date(isoUtc).getTime() - Date.now()) / 1000));
}

@Injectable({ providedIn: 'root' })
export class AuthApiService extends IAuthService {
  private readonly http   = inject(HttpClient);
  private readonly tokens = inject(TokenService);

  // ─── Login ─────────────────────────────────────────────────────────────────
  override login(req: LoginRequest): Observable<LoginResult> {
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.login, { email: req.email, password: req.password })
      .pipe(
        map(dto => this.mapLoginResult(dto)),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Complete an MFA-gated login ────────────────────────────────────────────
  override verifyMfaLogin(challengeToken: string, code: string): Observable<LoginResponse> {
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.loginMfa, { challengeToken, code })
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

    // The JWT carries the roles + permissions claims — rebuild the user from those so
    // every login path (local + SSO) produces the same User shape.
    const payload = this.tokens.decodePayload<Record<string, unknown>>(dto.accessToken!) ?? {};
    const user = buildUserFromJwt(payload);
    user.mustChangePassword = dto.requiresPasswordChange ?? false;
    return {
      requiresMfa: false,
      accessToken:  dto.accessToken!,
      refreshToken: dto.refreshToken ?? '',
      expiresIn:    secondsUntil(dto.expiresOnUtc ?? undefined),
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
    return this.http
      .post<LocalLoginResponseDto>(AUTH_ENDPOINTS.changePassword, {
        currentPassword: req.currentPassword,
        newPassword: req.newPassword,
      })
      .pipe(
        tap(dto => {
          // Backend issues a fresh token with an updated pwd_change_required claim on password
          // change — persist it, or the old token's stale claim keeps forcing a change prompt.
          if (dto?.accessToken) {
            this.tokens.setTokens(
              dto.accessToken,
              dto.refreshToken ?? this.tokens.getRefreshToken() ?? '',
              this.tokens.isRemembered,
            );
          }
        }),
        map(() => ({ success: true, message: 'Password changed successfully.' })),
        catchError(err => throwError(() => err)),
      );
  }

  // ─── Get current user ──────────────────────────────────────────────────────
  override getCurrentUser(): Observable<User> {
    const token = this.tokens.getAccessToken();
    if (!token || this.tokens.isExpired(token)) {
      return throwError(() => ({ code: 'UNAUTHORIZED', message: 'Not authenticated.' }));
    }
    const payload = this.tokens.decodePayload<Record<string, unknown>>(token);
    if (!payload) return throwError(() => ({ code: 'UNAUTHORIZED', message: 'Not authenticated.' }));
    return of(buildUserFromJwt(payload));
  }

  // ─── Refresh ───────────────────────────────────────────────────────────────
  override refreshToken(refreshToken: string): Observable<TokenPair> {
    return this.http.post<LocalLoginResponseDto>(AUTH_ENDPOINTS.refresh, { refreshToken }).pipe(
      map(dto => ({
        accessToken:  dto.accessToken!,
        refreshToken: dto.refreshToken ?? '',
        expiresIn:    secondsUntil(dto.expiresOnUtc ?? undefined),
      })),
      catchError(err => throwError(() => err)),
    );
  }
}
