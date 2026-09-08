// The backend's role model is open-ended — RolesController lets admins create arbitrary custom
// roles (RoleManagementService.CreateRoleAsync) alongside the 4 built-in system roles — so this
// must stay a plain string (the exact `Role.Name` value verbatim), never a closed union. Use
// SYSTEM_ROLE_NAMES below when code specifically needs to reference one of the 4 built-ins.
export type UserRole = string;

/** The 4 built-in, non-deletable roles (UnifiedRoles.cs). Any other role name is a customer-created custom role. */
export const SYSTEM_ROLE_NAMES = ['SuperAdmin', 'Admin', 'Operations', 'Audit'] as const;

/** The one role whose permission grant is immutable (always "every permission that exists" — see
 *  SystemRoleDefaultPermissions.cs). The other 3 built-ins (Admin, Operations, Audit) are still
 *  non-deletable/non-renameable, but SuperAdmin may edit their permission grants — see
 *  role-permissions.component.ts's isPermissionsLocked. */
export const SUPER_ADMIN_ROLE_NAME = 'SuperAdmin';

export type UserStatus = 'active' | 'inactive' | 'suspended' | 'pending';
export type LoginType   = 'local' | 'sso' | 'oauth';

// ─── Permission ────────────────────────────────────────────────────────────────
export interface Permission {
  id:          string;
  name:        string;   // 'user.view' (wire-format code)
  displayName: string;   // 'View User'
  resource:    string;   // 'user' — derived from name, matches the owning PermissionGroup
  action:      string;   // 'view' — derived from name
  description: string;
}

/** One row (Permission Group) of a permission-category table. */
export interface PermissionGroupNode {
  id:          string;
  name:        string;
  displayName: string;
  permissions: Permission[];
}

/** One table (Permission Category) in the permission-management grid. */
export interface PermissionCategory {
  id:          string;
  name:        string;
  displayName: string;
  groups:      PermissionGroupNode[];
}

// ─── Role ──────────────────────────────────────────────────────────────────────
export interface Role {
  id:           string;
  name:         UserRole;
  displayName:  string;
  description:  string;
  permissions:  Permission[];
  color:        string;
  isSystemRole: boolean;
  createdAt:    string;
  createdBy?:     string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?:    string | null;
}

// ─── User ─────────────────────────────────────────────────────────────────────
export interface User {
  id:                 string;
  email:              string;
  firstName:          string;
  lastName:           string;
  fullName:           string;
  avatar?:            string;
  role:               UserRole;
  roles:              Role[];
  permissions:        Permission[];
  /** Direct overrides on top of role-derived permissions — grant/deny, keyed by permission id. */
  directPermissionAllocations: PermissionAllocationDto[];
  orgId:              string;
  department?:        string;
  jobTitle?:          string;
  phone?:             string;
  status:             UserStatus;
  loginType:          LoginType;
  mustChangePassword: boolean;
  /** Decoded from the `mfa_setup_required` JWT claim — already accounts for both the admin policy
   *  AND current enrollment status (true only while blocked). The app must route the user straight
   *  to MFA enrollment while this is true — see mfaSetupGuard. Optional (defaults to falsy) so it
   *  doesn't force every mock/test User literal in the codebase to specify it. */
  mfaSetupRequired?:  boolean;
  /** Admin policy (raw): this account is required to have 2FA enabled. Distinct from
   *  mfaSetupRequired above — this is the toggle an admin sets on another user's account (see
   *  UserDetailComponent); mfaSetupRequired is the derived "currently blocked" flag for yourself. */
  mfaRequired?:       boolean;
  emailVerified:      boolean;
  twoFactorEnabled:   boolean;
  invitedBy?:         string;
  lastLoginAt?:       string;
  createdAt:          string;
  updatedAt:          string;
  createdBy?:         string | null;
  modifiedBy?:        string | null;
  /** Only present in mock DB — never exposed by real API */
  passwordHash?:      string;
}

// ─── Shared response wrappers ─────────────────────────────────────────────────
export interface PaginatedResponse<T> {
  data:        T[];
  total:       number;
  page:        number;
  perPage:     number;
  totalPages:  number;
}

export interface MessageResponse {
  message: string;
  success: boolean;
}

export interface TokenPair {
  accessToken:  string;
  refreshToken: string;
  expiresIn:    number;
}

export interface UserQueryParams {
  page?:       number;
  perPage?:    number;
  search?:     string;
  role?:       UserRole;
  status?:     UserStatus;
  sortBy?:     string;
  sortOrder?:  'asc' | 'desc';
}

// ─── Backend API DTOs ──────────────────────────────────────────────────────────
export interface PermissionDto {
  id:          string;
  name:        string;
  displayName: string;
  description: string;
  groupId?:    string | null;
  isVisible:   boolean;
}

export interface PermissionCatalogGroupDto {
  id:          string;
  name:        string;
  displayName: string;
  permissions: PermissionDto[];
}

export interface PermissionCatalogCategoryDto {
  id:          string;
  name:        string;
  displayName: string;
  groups:      PermissionCatalogGroupDto[];
}

/** A user's direct permission allocation — grant/deny override on top of role-derived permissions. */
export interface PermissionAllocationDto {
  permissionId:          string;
  permissionName:        string;
  permissionDescription: string;
  isEnabled:             boolean;
}

export interface UpsertUserPermissionAllocationRequest {
  isEnabled: boolean;
}

export interface RoleDto {
  id:           string;
  name:         string;
  description:  string;
  permissions:  PermissionDto[];
  isSystemRole: boolean;
  createdOnUtc?:  string | null;
  createdBy?:     string | null;
  modifiedOnUtc?: string | null;
  modifiedBy?:    string | null;
}

/** Backend user status. The API serializes the C# UserStatus enum via JsonStringEnumConverter
 *  (see FHIRBridge.Api Program.cs), so this is the enum member's name on the wire, not its
 *  underlying number — matching against 1|2|3 here would never hit and silently fall through
 *  to whatever default a caller picks. */
export type BackendUserStatus = 'Invited' | 'Active' | 'Inactive';

export interface UserManagementDto {
  id:                   string;
  externalUserId:       string;
  email:                string;
  displayName:          string;
  status:               BackendUserStatus;
  isEnabled:            boolean;
  isLocalLoginEnabled:  boolean;
  mustChangePassword:   boolean;
  globalRoleNames:      string[];
  createdOnUtc:         string;
  lastLoginOnUtc:       string | null;
  mfaEnabled:           boolean;
  mustSetupMfa:         boolean;
  createdBy?:           string | null;
  modifiedOnUtc?:       string | null;
  modifiedBy?:          string | null;
}

export interface UserDetailDto {
  id:                        string;
  email:                     string;
  firstName:                 string;
  lastName:                  string;
  displayName:               string;
  status:                    BackendUserStatus;
  isEnabled:                 boolean;
  roles:                     RoleDto[];
  directPermissionAllocations: PermissionAllocationDto[];
  createdOnUtc:              string;
  lastLoginOnUtc:            string | null;
  mustSetupMfa:              boolean;
  invitationToken?:          string;
  mfaEnabled:                boolean;
  /** False when this response comes from an invite/resend call whose email failed to send. Absent on a
   *  plain user read (the backend defaults it to true there — not meaningful outside the invite flow). */
  invitationEmailSent?:      boolean;
}

/** Result of an invite / resend-invite call, surfaced to the UI so it can show the invite link. */
export interface InviteResult {
  success:          boolean;
  message:          string;
  email:            string;
  invitationToken?: string;
  /** Absolute link the invited user should open to set their password. */
  invitationLink?:  string;
  /** False when the user was created but the invitation email itself failed to send (e.g. SMTP down) —
   *  the token/link above is still valid, but the admin must relay it manually or use Resend Invitation. */
  emailSent:        boolean;
}

/**
 * Result of an admin-triggered password reset request, surfaced to the UI so it can show the
 * reset link directly (email delivery isn't wired up yet — see LocalAuth:ExposeResetTokens).
 */
export interface PasswordResetLinkResult {
  success:     boolean;
  message:     string;
  email:       string;
  resetToken?: string;
  /** Absolute link the user should open to choose a new password. Undefined when the backend
   *  doesn't expose raw reset tokens (e.g. LocalAuth:ExposeResetTokens=false, as in production). */
  resetLink?:  string;
}
