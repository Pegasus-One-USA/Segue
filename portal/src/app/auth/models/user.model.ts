// The backend's role model is open-ended — RolesController lets admins create arbitrary custom
// roles (RoleManagementService.CreateRoleAsync) alongside the 4 built-in system roles — so this
// must stay a plain string (the exact `Role.Name` value verbatim), never a closed union. Use
// SYSTEM_ROLE_NAMES below when code specifically needs to reference one of the 4 built-ins.
export type UserRole = string;

/** The 4 built-in, non-deletable roles (UnifiedRoles.cs). Any other role name is a customer-created custom role. */
export const SYSTEM_ROLE_NAMES = ['SuperAdmin', 'Admin', 'Operations', 'Audit'] as const;

export type UserStatus = 'active' | 'inactive' | 'suspended' | 'pending';
export type LoginType   = 'local' | 'sso' | 'oauth';

// ─── Permission ────────────────────────────────────────────────────────────────
export interface Permission {
  id:          string;
  name:        string;   // 'users:read'
  resource:    string;   // 'users'
  action:      string;   // 'read'
  description: string;
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
  orgId:              string;
  department?:        string;
  jobTitle?:          string;
  phone?:             string;
  status:             UserStatus;
  loginType:          LoginType;
  mustChangePassword: boolean;
  emailVerified:      boolean;
  twoFactorEnabled:   boolean;
  invitedBy?:         string;
  lastLoginAt?:       string;
  createdAt:          string;
  updatedAt:          string;
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
  description: string;
  categoryId?: string | null;
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
}

/** Backend numeric user status: 1 = Invited, 2 = Active, 3 = Inactive. */
export type BackendUserStatus = 1 | 2 | 3;

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
  invitationToken?:          string;
}

/** Result of an invite / resend-invite call, surfaced to the UI so it can show the invite link. */
export interface InviteResult {
  success:          boolean;
  message:          string;
  email:            string;
  invitationToken?: string;
  /** Absolute link the invited user should open to set their password. */
  invitationLink?:  string;
}
