import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { tap } from 'rxjs/operators';
import { Observable, EMPTY, of } from 'rxjs';
import { IAuthService } from './i-auth.service';
import { SessionService } from './session.service';
import { TokenService } from './token.service';
import { AuthStore } from '../store/auth.store';
import { AccountSecurityService } from './account-security.service';
import { EmailNotificationService } from './email-notification.service';
import { UserRole, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';
import { buildUserFromJwt } from './jwt-user.mapper';

const LOCKOUT_MINUTES = 30;

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api      = inject(IAuthService);
  private readonly store    = inject(AuthStore);
  private readonly tokens   = inject(TokenService);
  private readonly session  = inject(SessionService);
  private readonly router   = inject(Router);
  private readonly security = inject(AccountSecurityService);
  private readonly emailSvc = inject(EmailNotificationService);

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
    // Check account lockout before hitting the API
    const lockInfo = this.security.getLockoutInfo(req.email);
    if (lockInfo.locked) {
      const msg = `Account locked. Try again in ${lockInfo.minutesRemaining} minute${lockInfo.minutesRemaining !== 1 ? 's' : ''}.`;
      this.store.setError(msg);
      return EMPTY;
    }

    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.login(req).pipe(
      tap({
        next: (res) => {
          this.security.clearAttempts(req.email, res.user.id);
          this.store.setUser(res.user);
          this.session.start(res.user.id, res.accessToken, res.refreshToken, req.rememberMe ?? false);
          this.store.setLoading(false);
          this.router.navigate(['/dashboard']);
        },
        error: (err) => {
          const info = this.security.recordFailedAttempt(req.email);
          let message = err?.message ?? 'Login failed. Please try again.';

          if (info.locked) {
            message = `Account locked for ${LOCKOUT_MINUTES} minutes due to too many failed attempts.`;
            this.emailSvc.sendAccountLockedEmail(req.email, LOCKOUT_MINUTES).subscribe();
          } else if (info.attemptsLeft <= 2 && info.attemptsLeft > 0) {
            message += ` ${info.attemptsLeft} attempt${info.attemptsLeft !== 1 ? 's' : ''} remaining before lockout.`;
          }

          this.store.setError(message);
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
  // Rebuild the current user (roles + permissions) synchronously from the stored JWT claims via
  // buildUserFromJwt — the same mapper login/SSO use, so no API round-trip or mock lookup is
  // needed. Returns an Observable so app.config's APP_INITIALIZER can treat it uniformly.
  initFromToken(): Observable<void> {
    const token = this.tokens.getAccessToken();
    if (!token || this.tokens.isExpired(token)) {
      this.tokens.clearTokens();
      return of(undefined);
    }
    const payload = this.tokens.decodePayload<Record<string, unknown>>(token);
    if (payload) {
      this.store.setUser(buildUserFromJwt(payload));
    }
    return of(undefined);
  }
}
