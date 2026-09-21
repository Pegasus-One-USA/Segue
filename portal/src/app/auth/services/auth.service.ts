import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { tap, finalize, catchError, map } from 'rxjs/operators';
import { Observable, EMPTY, of } from 'rxjs';
import { IAuthService } from './i-auth.service';
import { SessionService } from './session.service';
import { TokenService } from './token.service';
import { AuthStore } from '../store/auth.store';
import { PermissionService } from './permission.service';
import { AccountSecurityService } from './account-security.service';
import { EmailNotificationService } from './email-notification.service';
import { BrandingService } from '../../services/branding.service';
import { AppInitService } from '../../onboarding/services/app-init.service';
import { User, UserRole, MessageResponse } from '../models/user.model';
import {
  LoginRequest, LoginResponse, LoginResult,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
  MagicLinkRequest, MagicLinkRedeemRequest,
} from '../models/auth-request.model';
import { extractApiErrorMessage } from '../../core/http-error.util';

const LOCKOUT_MINUTES = 30;

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api        = inject(IAuthService);
  private readonly store      = inject(AuthStore);
  private readonly tokens     = inject(TokenService);
  private readonly session    = inject(SessionService);
  private readonly router     = inject(Router);
  private readonly security   = inject(AccountSecurityService);
  private readonly emailSvc   = inject(EmailNotificationService);
  private readonly permission = inject(PermissionService);
  private readonly branding   = inject(BrandingService);
  private readonly appInit    = inject(AppInitService);

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
  // When the account has MFA enabled, the resolved value carries requiresMfa: true plus a
  // challenge token instead of a session — no user/tokens are stored and no navigation happens
  // until the caller (LoginComponent) submits a code via completeMfaLogin().
  login(req: LoginRequest): Observable<LoginResult> {
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
          this.store.setLoading(false);
          if (res.requiresMfa) return;

          this.security.clearAttempts(req.email, res.user.id);
          this.store.setUser(res.user);
          this.session.start(res.user.id, req.rememberMe ?? false);
          this.resolveBrandingForNewSession();
          this.appInit.refreshLicenseGate();
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

  // ─── Complete an MFA-gated login ────────────────────────────────────────────
  // Takes the email alongside the challenge token/code purely to key the client-side lockout
  // tracker (AccountSecurityService) the same way login()'s error path does — the server has its
  // own independent lockout check keyed by the account itself, this is just the UI-side counter.
  completeMfaLogin(email: string, challengeToken: string, code: string, rememberMe = false): Observable<LoginResponse> {
    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.verifyMfaLogin(challengeToken, code, rememberMe).pipe(
      tap({
        next: (res) => {
          this.security.clearAttempts(email, res.user.id);
          this.store.setUser(res.user);
          this.session.start(res.user.id, rememberMe);
          this.resolveBrandingForNewSession();
          this.appInit.refreshLicenseGate();
          this.store.setLoading(false);
          this.router.navigate(['/dashboard']);
        },
        error: (err) => {
          this.security.recordFailedAttempt(email);
          this.store.setError(extractApiErrorMessage(err, 'Invalid or expired code. Please try again.'));
          this.store.setLoading(false);
        },
      })
    );
  }

  // ─── Magic-link request ─────────────────────────────────────────────────────
  requestMagicLink(req: MagicLinkRequest): Observable<MessageResponse> {
    return this.api.requestMagicLink(req);
  }

  // ─── Magic-link redeem ──────────────────────────────────────────────────────
  // Same shape as login(): may resolve to an MFA challenge (no session yet) or a completed session.
  redeemMagicLink(req: MagicLinkRedeemRequest): Observable<LoginResult> {
    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.redeemMagicLink(req).pipe(
      tap({
        next: (res) => {
          this.store.setLoading(false);
          if (res.requiresMfa) return;

          this.store.setUser(res.user);
          this.session.start(res.user.id, false);
          this.resolveBrandingForNewSession();
          this.appInit.refreshLicenseGate();
          this.router.navigate(['/dashboard']);
        },
        error: (err) => {
          this.store.setError(extractApiErrorMessage(err, 'This sign-in link is invalid or has expired.'));
          this.store.setLoading(false);
        },
      })
    );
  }

  // ─── Logout ────────────────────────────────────────────────────────────────
  // Clears local session state and navigates to login unconditionally via finalize(), regardless of
  // whether the backend POST /auth/logout call succeeds — an already-expired/invalid access token makes
  // that call 401 (the auth interceptor deliberately never retries/refreshes calls under /auth/, see
  // auth.interceptor.ts), and with only a `next` handler that 401 previously left the user stuck "signed
  // in" with a dead session, since the token was never cleared and the redirect never fired.
  logout(): void {
    this.api.logout().pipe(
      // Swallow the error here (a dead/expired token 401s) rather than in subscribe()'s error callback —
      // finalize() below already does the actual cleanup unconditionally, so there's nothing left for an
      // error handler to do.
      catchError(() => EMPTY),
      finalize(() => {
        this.session.end();
        this.router.navigate(['/auth/login']);
      }),
    ).subscribe();
  }

  // ─── Register ──────────────────────────────────────────────────────────────
  register(req: RegisterRequest): Observable<RegisterResponse> {
    this.store.setLoading(true);
    this.store.setError(null);

    return this.api.register(req).pipe(
      tap({
        next: (res) => {
          this.store.setUser(res.user);
          this.session.start(res.user.id);
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
  // Called by authInterceptor on a 401. The backend rotates both cookies on every call; the
  // rebuilt User (from the fresh profile) may carry different permission claims than before
  // (e.g. an admin changed this user's role since login) — updating AuthStore here is what
  // actually makes "permissions update on refresh" true, not just the cookie.
  refreshToken(): Observable<User> {
    return this.api.refreshToken().pipe(
      tap(user => {
        this.tokens.markSessionActive();
        this.store.setUser(user);
      })
    );
  }

  // ─── Get current user (used for cross-tab sync and session bootstrap) ─────
  getCurrentUser(): Observable<User> {
    return this.api.getCurrentUser();
  }

  // ─── Re-sync AuthStore without a token refresh ────────────────────────────
  // Used by authInterceptor as TokenRefreshCoordinator's onAlreadyRefreshed callback: when this tab's
  // cross-tab lock wait discovers another tab already refreshed moments ago, calling refreshToken()
  // again would send the now-already-rotated-away cookie and fail. GET /auth/me instead applies the
  // same "AuthStore reflects the latest profile" side effect refreshToken()'s own tap() would have,
  // without touching the refresh token at all — so this tab doesn't silently keep stale permission/
  // role claims for the rest of the session just because another tab happened to win the race.
  syncCurrentUser(): Observable<User> {
    return this.api.getCurrentUser().pipe(tap(user => this.store.setUser(user)));
  }

  // ─── Permission helpers ───────────────────────────────────────────────────
  // hasPermission() delegates to PermissionService (the one centralized, admin-bypass-aware,
  // O(1) implementation) rather than AuthStore.hasPermission() — AuthStore keeps its own
  // simple version for backward compatibility with call sites that inject it directly, but
  // every NEW call site (this facade, the two directives, the guard) should route through
  // PermissionService so there's a single place that logic can evolve.
  hasRole(...roles: UserRole[]): boolean    { return this.store.hasRole(...roles); }
  hasPermission(perm: string): boolean      { return this.permission.hasPermission(perm); }
  isAdmin(): boolean                        { return this.store.isAdmin(); }

  // ─── Initialise session (called in app init) ──────────────────────────────
  // HIPAA #7: the access token is an HttpOnly cookie now — this client can no longer decode it
  // synchronously from storage, so bootstrapping the session means actually asking the server via
  // GET /auth/me. A 401 (no cookie, or an expired one the browser already dropped) just means
  // "not logged in" — not an error the caller needs to handle specially.
  initFromToken(): Observable<void> {
    if (!this.tokens.hasSession()) {
      return of(undefined);
    }

    return this.api.getCurrentUser().pipe(
      tap(user => this.store.setUser(user)),
      map(() => undefined),
      catchError(() => {
        this.tokens.clearTokens();
        return of(undefined);
      }),
    );
  }

  // ─── Discard any stored session (called at boot when the backend requires first-run setup) ──
  // An un-provisioned deployment (no users) cannot have a valid session, so a lingering JWT from a
  // previous deployment is stale and must be cleared — otherwise authGuard would treat the old
  // token as authenticated and skip the first-run /setup screen.
  discardSession(): void {
    this.tokens.clearTokens();
    this.store.clear();
  }

  // ─── Re-resolve branding once a session is authenticated ─────────────────────
  // BrandingService.resolve() otherwise only ever runs once, in its own constructor at app
  // bootstrap — before this session existed. If that bootstrap call happened while logged out
  // (e.g. the tab was refreshed after a logout), GET /api/v1/branding was anonymous and returned
  // the built-in default, and nothing re-fetched it afterward: logging back in as the same tenant
  // would otherwise leave that stale default applied indefinitely, even though the database was
  // never wrong. Calling it again here — now that the access-token cookie is set — lets the
  // backend resolve the real tenant (uid claim -> User.TenantId -> ICurrentTenantResolver) instead.
  //
  // A single call per successful session-establishing flow (login / MFA completion / magic-link
  // redeem each call this once, from their own success handler) — never concurrent, never
  // duplicated. resolve() already swallows its own HTTP errors internally (falls back to the
  // built-in default rather than erroring — see BrandingService), so this is fire-and-forget by
  // design: a branding hiccup can never fail or delay a login that has already succeeded.
  private resolveBrandingForNewSession(): void {
    this.branding.resolve().subscribe(config => this.branding.applyToDocument(config));
  }
}
