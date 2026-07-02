import { Injectable, inject, computed } from '@angular/core';
import { Router } from '@angular/router';
import { tap } from 'rxjs/operators';
import { Observable } from 'rxjs';
import { IAuthService } from './i-auth.service';
import { SessionService } from './session.service';
import { TokenService } from './token.service';
import { AuthStore } from '../store/auth.store';
import { User, UserRole, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';
import { MOCK_USERS } from '../mock/mock-db';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api     = inject(IAuthService);
  private readonly store   = inject(AuthStore);
  private readonly tokens  = inject(TokenService);
  private readonly session = inject(SessionService);
  private readonly router  = inject(Router);

  // ─── Expose store signals directly ────────────────────────────────────────
  readonly currentUser     = this.store.currentUser;
  readonly isAuthenticated = this.store.isAuthenticated;
  readonly isLoading       = this.store.isLoading;
  readonly error           = this.store.error;
  readonly roles           = this.store.roles;
  readonly permissions     = this.store.permissions;
  readonly initials        = this.store.initials;
  readonly displayName     = this.store.displayName;

  // ─── Login ─────────────────────────────────────────────────────────────────
  login(req: LoginRequest): Observable<LoginResponse> {
    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.login(req).pipe(
      tap({
        next: (res) => {
          this.store.setUser(res.user);
          this.session.start(res.user.id, res.accessToken, res.refreshToken, req.rememberMe ?? false);
          this.store.setLoading(false);
          this.router.navigate(['/dashboard']);
        },
        error: (err) => {
          this.store.setError(err?.message ?? 'Login failed. Please try again.');
          this.store.setLoading(false);
        },
      })
    );
  }

  // ─── Logout ────────────────────────────────────────────────────────────────
  logout(): void {
    this.api.logout().subscribe(() => {
      this.session.end();
      this.router.navigate(['/auth/login']);
    });
  }

  // ─── Register ──────────────────────────────────────────────────────────────
  register(req: RegisterRequest): Observable<RegisterResponse> {
    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.register(req).pipe(
      tap({
        next: (res) => {
          this.store.setUser(res.user);
          this.session.start(res.user.id, res.accessToken, res.refreshToken);
          this.store.setLoading(false);
          this.router.navigate(['/dashboard']);
        },
        error: (err) => {
          this.store.setError(err?.message ?? 'Registration failed. Please try again.');
          this.store.setLoading(false);
        },
      })
    );
  }

  // ─── Forgot password ───────────────────────────────────────────────────────
  forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return this.api.forgotPassword(req);
  }

  // ─── Reset password ────────────────────────────────────────────────────────
  resetPassword(req: ResetPasswordRequest): Observable<MessageResponse> {
    return this.api.resetPassword(req);
  }

  // ─── Change password ───────────────────────────────────────────────────────
  changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    return this.api.changePassword(req);
  }

  // ─── Refresh ───────────────────────────────────────────────────────────────
  refreshToken(refreshToken: string): Observable<TokenPair> {
    return this.api.refreshToken(refreshToken).pipe(
      tap(pair => this.tokens.setTokens(pair.accessToken, pair.refreshToken))
    );
  }

  // ─── Permission helpers ───────────────────────────────────────────────────
  hasRole(...roles: UserRole[]): boolean    { return this.store.hasRole(...roles); }
  hasPermission(perm: string): boolean      { return this.store.hasPermission(perm); }
  isAdmin(): boolean                        { return this.store.isAdmin(); }

  // ─── Initialise from stored token (called in app init) ───────────────────
  initFromToken(): void {
    const token = this.tokens.getAccessToken();
    if (!token || this.tokens.isExpired(token)) {
      this.tokens.clearTokens();
      return;
    }
    const payload = this.tokens.decodePayload<{ sub: string }>(token);
    if (!payload) return;

    // Restore user from mock DB by subject claim
    const user = MOCK_USERS.find(u => u.id === payload.sub);
    if (user) {
      const { passwordHash: _, ...safe } = user;
      this.store.setUser(safe);
    }
  }
}
