/**
 * AuthApiService — real HTTP implementation of IAuthService against the FHIRBridge backend
 * (`${apiBase}/api/v1/auth/internal/*`). The access token is a JWT carrying `roles` + `permissions`
 * claims; the current user is rebuilt from those claims (see jwt-user.mapper). Wired in app.config.ts.
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, of, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { IAuthService } from './i-auth.service';
import { TokenService } from './token.service';
import { User, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';
import { buildUserFromJwt } from './jwt-user.mapper';

const BASE = `${environment.apiBase}/api/v1/auth/internal`;

interface BackendLoginResponse {
  accessToken: string;
  tokenType: string;
  expiresOnUtc: string;
  requiresPasswordChange: boolean;
  refreshToken?: string;
  refreshTokenExpiresOnUtc?: string;
}

@Injectable({ providedIn: 'root' })
export class AuthApiService extends IAuthService {
  private readonly http = inject(HttpClient);
  private readonly tokens = inject(TokenService);

  override login(req: LoginRequest): Observable<LoginResponse> {
    return this.http
      .post<BackendLoginResponse>(`${BASE}/login`, { email: req.email, password: req.password })
      .pipe(map(res => {
        const payload = this.tokens.decodePayload<Record<string, unknown>>(res.accessToken) ?? {};
        const user = buildUserFromJwt(payload);
        user.mustChangePassword = res.requiresPasswordChange ?? false;
        const expiresIn = res.expiresOnUtc
          ? Math.max(60, Math.floor((new Date(res.expiresOnUtc).getTime() - Date.now()) / 1000))
          : 3600;
        return { accessToken: res.accessToken, refreshToken: res.refreshToken ?? '', expiresIn, user };
      }));
  }

  override logout(): Observable<void> {
    // Stateless JWT — nothing to invalidate server-side; the facade clears the local session.
    return of(void 0);
  }

  override register(_req: RegisterRequest): Observable<RegisterResponse> {
    return throwError(() => ({
      code: 'NOT_SUPPORTED',
      message: 'Self-registration is disabled. Ask an administrator to invite you.',
    }));
  }

  override forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return this.http.post(`${BASE}/forgot-password`, { email: req.email }).pipe(
      map(() => ({ success: true, message: `If an account exists for ${req.email}, a password-reset link has been sent.` })),
    );
  }

  override resetPassword(req: ResetPasswordRequest): Observable<MessageResponse> {
    return this.http.post(`${BASE}/reset-password`, { token: req.token, newPassword: req.newPassword }).pipe(
      map(() => ({ success: true, message: 'Password has been reset successfully. You may now sign in.' })),
    );
  }

  override changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    return this.http
      .post(`${BASE}/change-password`, { currentPassword: req.currentPassword, newPassword: req.newPassword })
      .pipe(map(() => ({ success: true, message: 'Password changed successfully.' })));
  }

  override getCurrentUser(): Observable<User> {
    const token = this.tokens.getAccessToken();
    if (!token || this.tokens.isExpired(token)) {
      return throwError(() => ({ code: 'UNAUTHORIZED', message: 'Not authenticated.' }));
    }
    const payload = this.tokens.decodePayload<Record<string, unknown>>(token);
    if (!payload) return throwError(() => ({ code: 'UNAUTHORIZED', message: 'Not authenticated.' }));
    return of(buildUserFromJwt(payload));
  }

  override refreshToken(_refreshToken: string): Observable<TokenPair> {
    // No refresh endpoint on the backend; the interceptor redirects to login on 401.
    return throwError(() => ({ code: 'NOT_SUPPORTED', message: 'Token refresh is not available.' }));
  }
}
