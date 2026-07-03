/**
 * AuthApiService — Real HTTP implementation, wired to AuthController (api/v1/auth/*).
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { map, catchError, tap } from 'rxjs/operators';
import { AUTH_ENDPOINTS } from '../../core/api-endpoints';
import { IAuthService } from './i-auth.service';
import { TokenService } from './token.service';
import { toUserRole, splitName, DEFAULT_ROLE_COLOR } from './api-user.service';
import { User, Role, Permission, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';

// ─── Backend DTO shapes (camelCase over the wire) ──────────────────────────────
interface AuthProfileDto {
  userId:         string;
  externalUserId: string;
  email:          string | null;
  displayName:    string | null;
  claimRoles:     string[];
}

interface LocalLoginResponseDto {
  accessToken:              string;
  tokenType:                string;
  expiresOnUtc:             string;
  requiresPasswordChange:   boolean;
  profile:                  AuthProfileDto;
  refreshToken?:            string;
  refreshTokenExpiresOnUtc?: string;
}

interface ForgotPasswordResponseDto {
  accepted:      boolean;
  resetToken?:   string | null;
  expiresOnUtc?: string | null;
}

interface DecodedTokenClaims {
  permissions?: string | string[];
  roles?:       string | string[];
  pwd_change_required?: string;
}

// JwtSecurityTokenHandler serializes a claim type as a bare string when there's exactly
// one value, and only as an array when there are two or more — normalize both shapes.
function toArray(v: string | string[] | undefined): string[] {
  if (!v) return [];
  return Array.isArray(v) ? v : [v];
}

// The backend only returns role names (no id/permissions/isSystemRole) at login time,
// so this fabricates a minimal Role rather than the richer RoleDto-backed one.
function mapClaimRoleToRole(name: string): Role {
  return {
    id: '', name: toUserRole(name), displayName: name, description: '',
    permissions: [], color: DEFAULT_ROLE_COLOR, isSystemRole: false, createdAt: '',
  };
}

function mapPermissionCode(code: string): Permission {
  const [resource, action] = code.split(/[.:]/);
  return { id: code, name: code, resource: resource ?? '', action: action ?? '', description: '' };
}

// Backend returns an absolute expiry timestamp; LoginResponse/TokenPair need relative seconds.
function secondsUntil(isoUtc: string): number {
  return Math.max(0, Math.round((new Date(isoUtc).getTime() - Date.now()) / 1000));
}

function buildUser(
  profile: AuthProfileDto,
  opts: { mustChangePassword: boolean; permissionCodes: string[] },
): User {
  const { firstName, lastName } = splitName(profile.displayName ?? '');
  const roles = (profile.claimRoles ?? []).map(mapClaimRoleToRole);
  const permissions = opts.permissionCodes.map(mapPermissionCode);

  return {
    id:                 profile.userId,
    email:              profile.email ?? '',
    firstName,
    lastName,
    fullName:           profile.displayName || profile.email || '',
    role:               roles[0]?.name ?? 'viewer',
    roles,
    permissions,
    orgId:              '',
    status:             'active',
    loginType:          'local',
    mustChangePassword: opts.mustChangePassword,
    emailVerified:      true,
    twoFactorEnabled:   false,
    createdAt:          '',
    updatedAt:          '',
  };
}

const notImpl = (reason: string) =>
  throwError(() => new Error(reason));

@Injectable({ providedIn: 'root' })
export class AuthApiService extends IAuthService {
  private readonly http   = inject(HttpClient);
  private readonly tokens = inject(TokenService);

  // ─── Login ─────────────────────────────────────────────────────────────────
  override login(req: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LocalLoginResponseDto>(AUTH_ENDPOINTS.login, {
      email: req.email,
      password: req.password,
    }).pipe(
      map(dto => {
        // The login response's profile DTO carries role names only — permission codes are
        // embedded in the JWT itself, so they have to be decoded from the fresh token.
        const claims = this.tokens.decodePayload<DecodedTokenClaims>(dto.accessToken);
        const permissionCodes = toArray(claims?.permissions);
        const user = buildUser(dto.profile, {
          mustChangePassword: dto.requiresPasswordChange,
          permissionCodes,
        });
        return {
          accessToken:  dto.accessToken,
          refreshToken: dto.refreshToken ?? '',
          expiresIn:    secondsUntil(dto.expiresOnUtc),
          user,
        };
      }),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Logout ────────────────────────────────────────────────────────────────
  override logout(): Observable<void> {
    return this.http.post<void>(AUTH_ENDPOINTS.logout, {});
  }

  // ─── Register — no self-registration endpoint exists on the backend ────────
  override register(_req: RegisterRequest): Observable<RegisterResponse> {
    return notImpl('Self-registration is not supported — users are invited by an administrator.') as any;
  }

  // ─── Forgot password ───────────────────────────────────────────────────────
  override forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return this.http.post<ForgotPasswordResponseDto>(AUTH_ENDPOINTS.forgotPassword, req).pipe(
      map(dto => ({
        success: dto.accepted,
        message: dto.accepted
          ? `If an account exists for ${req.email}, a password-reset link has been sent.`
          : 'Unable to process the request.',
      })),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Reset password — backend also requires the email address, which the ──
  // reset-password page does not currently collect from the reset link.
  override resetPassword(_req: ResetPasswordRequest): Observable<MessageResponse> {
    return notImpl('Reset-password is not wired up yet — the reset link needs to carry the account email.') as any;
  }

  // ─── Change password ───────────────────────────────────────────────────────
  override changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    return this.http.post<LocalLoginResponseDto>(AUTH_ENDPOINTS.changePassword, {
      currentPassword: req.currentPassword,
      newPassword: req.newPassword,
    }).pipe(
      tap(dto => {
        // Backend issues a fresh token with an updated pwd_change_required claim on password
        // change — persist it, or the old token's stale claim keeps forcing a change prompt.
        this.tokens.setTokens(dto.accessToken, dto.refreshToken ?? this.tokens.getRefreshToken() ?? '', this.tokens.isRemembered);
      }),
      map(() => ({ success: true, message: 'Password changed successfully.' })),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Get current user ──────────────────────────────────────────────────────
  override getCurrentUser(): Observable<User> {
    return this.http.get<AuthProfileDto>(AUTH_ENDPOINTS.me).pipe(
      map(profile => {
        // GET /me returns only identity fields, not claims — pull mustChangePassword and
        // permissions from the still-valid stored token instead of a second endpoint.
        const token = this.tokens.getAccessToken();
        const claims = token ? this.tokens.decodePayload<DecodedTokenClaims>(token) : null;
        return buildUser(profile, {
          mustChangePassword: claims?.pwd_change_required === 'true',
          permissionCodes: toArray(claims?.permissions),
        });
      }),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Refresh ───────────────────────────────────────────────────────────────
  override refreshToken(refreshToken: string): Observable<TokenPair> {
    return this.http.post<LocalLoginResponseDto>(AUTH_ENDPOINTS.refresh, { refreshToken }).pipe(
      map(dto => ({
        accessToken:  dto.accessToken,
        refreshToken: dto.refreshToken ?? '',
        expiresIn:    secondsUntil(dto.expiresOnUtc),
      })),
      catchError(err => throwError(() => err))
    );
  }
}
