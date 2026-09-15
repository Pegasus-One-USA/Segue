import { User, UserRole, LoginType, UserStatus } from './user.model';

export interface LoginRequest {
  email:      string;
  password:   string;
  rememberMe?: boolean;
}

export interface LoginResponse {
  requiresMfa?: false;
  // HIPAA #7: tokens live in HttpOnly cookies the backend sets directly — never present on this object.
  user:         User;
}

/** Password was correct but the account has MFA enabled — no session yet. Submit the challenge
 *  token plus a TOTP/backup code (via AuthService.completeMfaLogin) to finish signing in. */
export interface MfaChallengeResponse {
  requiresMfa: true;
  mfaChallengeToken: string;
}

export type LoginResult = LoginResponse | MfaChallengeResponse;

export interface RegisterRequest {
  firstName:   string;
  lastName:    string;
  email:       string;
  password:    string;
  role?:       UserRole;
  acceptTerms: boolean;
}

export interface RegisterResponse {
  user: User;
}

export interface ForgotPasswordRequest {
  email: string;
}

/**
 * Mirrors the backend's ForgotPasswordResponse. resetToken/expiresOnUtc are only populated when
 * LocalAuth:ExposeResetTokens=true (local/dev config; see appsettings.Development.json) — shared
 * by both the self-service forgot-password flow (auth-api.service.ts) and the admin-triggered
 * "generate a reset link" action (api-user.service.ts), since both call the same endpoint.
 */
export interface ForgotPasswordResponseDto {
  accepted: boolean;
  resetToken?: string | null;
  expiresOnUtc?: string | null;
}

export interface ResetPasswordRequest {
  token:           string;
  // The backend looks the account up by email + reset-token hash (not by token alone), so this
  // must round-trip from the reset link's query params — see ResetPasswordComponent.
  email:           string;
  newPassword:     string;
  confirmPassword: string;
}

export interface MagicLinkRequest {
  email: string;
}

/** Mirrors the backend's MagicLinkResponse — always accepted, no user enumeration. */
export interface MagicLinkResponseDto {
  accepted: boolean;
}

export interface MagicLinkRedeemRequest {
  email: string;
  token: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword:     string;
  confirmPassword: string;
}

export interface InviteUserRequest {
  email:       string;
  firstName:   string;
  lastName:    string;
  role:        UserRole;
  /** Backend role id (guid). When present it is sent verbatim as `roleId`. */
  roleId?:     string;
  department?: string;
}

export interface CreateUserRequest {
  firstName:   string;
  lastName:    string;
  email:       string;
  role:        UserRole;
  department?: string;
  jobTitle?:   string;
  loginType:   LoginType;
  status:      UserStatus;
  sendInvite?: boolean;
}

export interface UpdateUserRequest {
  firstName?:  string;
  lastName?:   string;
  department?: string;
  jobTitle?:   string;
  phone?:      string;
  role?:       UserRole;
  status?:     UserStatus;
}

export interface CreateRoleRequest {
  name:          string;
  description:   string;
  permissionIds: string[];
  // RBAC redesign Step 6 (backend: CreateRoleRequest.IsFullAccess, default false). Omitting this
  // creates a normal role exactly as before this field existed — the backend rejects `true` from a
  // caller who doesn't already have Full System Access themselves (RoleManagementService.CreateRoleAsync).
  isFullAccess?: boolean;
}

export interface UpdateRoleRequest {
  name:          string;
  description:   string;
  permissionIds: string[];
  // RBAC redesign Step 6 (backend: UpdateRoleRequest.IsFullAccess, nullable). `null`/omitted means "not
  // touching this field" and is always accepted regardless of the role's current value or the caller's
  // own access — see role-permissions.component.ts's save(), which never sets this. A concrete
  // true/false is only gated by the backend when it actually differs from the role's current value
  // (RoleManagementService.UpdateRoleAsync) — role-dialog.component.ts always sends its form's current
  // value, changed or not, and relies entirely on that backend no-op-if-unchanged behavior.
  isFullAccess?: boolean | null;
}
