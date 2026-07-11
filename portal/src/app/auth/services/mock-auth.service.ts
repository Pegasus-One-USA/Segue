import { Injectable } from '@angular/core';
import { Observable, of, throwError } from 'rxjs';
import { delay, switchMap } from 'rxjs/operators';
import { IAuthService } from './i-auth.service';
import { User, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse, LoginResult,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';
import { MOCK_USERS, ALL_ROLES } from '../mock/mock-db';

// ─── MFA challenge simulation (local dev only, no backend) ─────────────────────
// Maps a fabricated challenge token to the userId it was issued for, so verifyMfaLogin can
// find the same user again without a real server-side store. Any well-formed 6-digit code
// (or the fixed dev backup code) is accepted — this mock exists to exercise the two-step UI
// flow locally, not to simulate real TOTP validation.
const MFA_DEV_BACKUP_CODE = 'DEV-BYPASS';
const pendingMfaChallenges = new Map<string, string>();

function isAcceptableMockCode(code: string): boolean {
  return /^\d{6}$/.test(code) || code.trim().toUpperCase() === MFA_DEV_BACKUP_CODE;
}

// ─── Fake JWT helpers ──────────────────────────────────────────────────────────
function fakeJWT(user: User, expiresIn = 3600): string {
  const header  = btoa(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = btoa(JSON.stringify({
    sub:         user.id,
    email:       user.email,
    role:        user.role,
    roles:       user.roles.map(r => r.name),
    permissions: user.permissions.map(p => p.name),
    orgId:       user.orgId,
    iat:         Math.floor(Date.now() / 1000),
    exp:         Math.floor(Date.now() / 1000) + expiresIn,
  }));
  return `${header}.${payload}.fhirbridge-mock-sig`;
}

function fakeRefresh(userId: string): string {
  return `rt_${userId}_${Date.now()}_${Math.random().toString(36).slice(2)}`;
}

function sanitise(u: User): User {
  const safe = { ...u };
  delete (safe as Partial<User>).passwordHash;
  return safe as User;
}

@Injectable({ providedIn: 'root' })
export class MockAuthService extends IAuthService {

  // ─── Login ─────────────────────────────────────────────────────────────────
  override login(req: LoginRequest): Observable<LoginResult> {
    return of(null).pipe(
      delay(800),
      switchMap(() => {
        const user = MOCK_USERS.find(
          u => u.email.toLowerCase() === req.email.trim().toLowerCase()
        );

        if (!user)
          return throwError(() => ({ code: 'USER_NOT_FOUND', message: 'No account found with this email address.' }));
        if (user.passwordHash !== req.password)
          return throwError(() => ({ code: 'INVALID_CREDENTIALS', message: 'Incorrect password. Please try again.' }));
        if (user.status === 'suspended')
          return throwError(() => ({ code: 'USER_SUSPENDED', message: 'Your account has been suspended. Contact your administrator.' }));
        if (user.status === 'inactive')
          return throwError(() => ({ code: 'USER_INACTIVE', message: 'Your account is inactive. Contact your administrator.' }));
        if (user.status === 'pending')
          return throwError(() => ({ code: 'EMAIL_NOT_VERIFIED', message: 'Please verify your email address before signing in.' }));

        if (user.twoFactorEnabled) {
          const mfaChallengeToken = `mfa_${user.id}_${Date.now()}`;
          pendingMfaChallenges.set(mfaChallengeToken, user.id);
          return of<LoginResult>({ requiresMfa: true, mfaChallengeToken });
        }

        const accessToken  = fakeJWT(user, req.rememberMe ? 86400 * 30 : 3600);
        const refreshToken = fakeRefresh(user.id);
        return of<LoginResult>({ requiresMfa: false, accessToken, refreshToken, expiresIn: 3600, user: sanitise(user) });
      })
    );
  }

  // ─── Complete an MFA-gated login ────────────────────────────────────────────
  override verifyMfaLogin(challengeToken: string, code: string): Observable<LoginResponse> {
    return of(null).pipe(
      delay(500),
      switchMap(() => {
        const userId = pendingMfaChallenges.get(challengeToken);
        const user = userId ? MOCK_USERS.find(u => u.id === userId) : undefined;
        if (!user)
          return throwError(() => ({ code: 'MFA_CHALLENGE_EXPIRED', message: 'Your session has expired. Please log in again.' }));
        if (!isAcceptableMockCode(code))
          return throwError(() => ({ code: 'MFA_INVALID_CODE', message: 'A valid MFA code is required.' }));

        pendingMfaChallenges.delete(challengeToken);
        const accessToken  = fakeJWT(user, 3600);
        const refreshToken = fakeRefresh(user.id);
        return of<LoginResponse>({ requiresMfa: false, accessToken, refreshToken, expiresIn: 3600, user: sanitise(user) });
      })
    );
  }

  // ─── Logout ────────────────────────────────────────────────────────────────
  override logout(): Observable<void> {
    return of(undefined).pipe(delay(300));
  }

  // ─── Register ──────────────────────────────────────────────────────────────
  override register(req: RegisterRequest): Observable<RegisterResponse> {
    return of(null).pipe(
      delay(1000),
      switchMap(() => {
        const exists = MOCK_USERS.find(u => u.email.toLowerCase() === req.email.toLowerCase());
        if (exists)
          return throwError(() => ({ code: 'EMAIL_TAKEN', message: 'An account with this email already exists.' }));

        const roleObj = ALL_ROLES.find(r => r.name === (req.role ?? 'Audit'))!;
        const newUser: User = {
          id:                 `u-${Date.now()}`,
          email:              req.email.trim().toLowerCase(),
          firstName:          req.firstName.trim(),
          lastName:           req.lastName.trim(),
          fullName:           `${req.firstName.trim()} ${req.lastName.trim()}`,
          role:               req.role ?? 'Audit',
          roles:              [roleObj],
          permissions:        roleObj.permissions,
          orgId:              'org-001',
          status:             'active',
          loginType:          'local',
          mustChangePassword: false,
          emailVerified:      true,
          twoFactorEnabled:   false,
          createdAt:          new Date().toISOString(),
          updatedAt:          new Date().toISOString(),
          passwordHash:       req.password,
        };

        MOCK_USERS.push(newUser);
        const accessToken  = fakeJWT(newUser, 3600);
        const refreshToken = fakeRefresh(newUser.id);
        return of<RegisterResponse>({ user: sanitise(newUser), accessToken, refreshToken });
      })
    );
  }

  // ─── Forgot password ───────────────────────────────────────────────────────
  override forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return of(null).pipe(
      delay(1200),
      switchMap(() => {
        const exists = MOCK_USERS.some(u => u.email.toLowerCase() === req.email.toLowerCase());
        // Always succeed to prevent email enumeration
        void exists;
        return of<MessageResponse>({
          success: true,
          message: `If an account exists for ${req.email}, a password-reset link has been sent.`,
        });
      })
    );
  }

  // ─── Reset password ────────────────────────────────────────────────────────
  override resetPassword(req: ResetPasswordRequest): Observable<MessageResponse> {
    return of(null).pipe(
      delay(800),
      switchMap(() => {
        if (!req.token?.startsWith('reset_'))
          return throwError(() => ({ code: 'INVALID_TOKEN', message: 'This reset link is invalid or has expired.' }));
        if (req.newPassword !== req.confirmPassword)
          return throwError(() => ({ code: 'PASSWORD_MISMATCH', message: 'Passwords do not match.' }));
        if (req.newPassword.length < 8)
          return throwError(() => ({ code: 'WEAK_PASSWORD', message: 'Password must be at least 8 characters.' }));

        // Extract userId from mock token  (format: reset_{userId}_{timestamp})
        const userId = req.token.split('_')[1];
        const user = MOCK_USERS.find(u => u.id === userId);
        if (user) user.passwordHash = req.newPassword;

        return of<MessageResponse>({ success: true, message: 'Password has been reset successfully. You may now sign in.' });
      })
    );
  }

  // ─── Change password ───────────────────────────────────────────────────────
  override changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        if (req.newPassword !== req.confirmPassword)
          return throwError(() => ({ code: 'PASSWORD_MISMATCH', message: 'New passwords do not match.' }));
        if (req.newPassword.length < 8)
          return throwError(() => ({ code: 'WEAK_PASSWORD', message: 'Password must be at least 8 characters.' }));
        return of<MessageResponse>({ success: true, message: 'Password changed successfully.' });
      })
    );
  }

  // ─── Get current user ──────────────────────────────────────────────────────
  override getCurrentUser(): Observable<User> {
    return of(null).pipe(
      delay(300),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.status === 'active');
        if (!user) return throwError(() => ({ code: 'UNAUTHORIZED', message: 'Not authenticated.' }));
        return of(sanitise(user));
      })
    );
  }

  // ─── Refresh token ─────────────────────────────────────────────────────────
  override refreshToken(refreshTok: string): Observable<TokenPair> {
    return of(null).pipe(
      delay(400),
      switchMap(() => {
        const userId = refreshTok.split('_')[1];
        const user   = MOCK_USERS.find(u => u.id === userId);
        if (!user) return throwError(() => ({ code: 'INVALID_TOKEN', message: 'Invalid refresh token.' }));
        return of<TokenPair>({
          accessToken:  fakeJWT(user, 3600),
          refreshToken: fakeRefresh(user.id),
          expiresIn:    3600,
        });
      })
    );
  }
}
