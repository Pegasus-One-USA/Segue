export type UserRole =
  | 'system-admin'
  | 'tenant-admin'
  | 'developer'
  | 'pipeline-editor'
  | 'reviewer'
  | 'auditor'
  | 'analyst'
  | 'viewer';

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
  id:               string;
  email:            string;
  firstName:        string;
  lastName:         string;
  displayName:      string;
  status:           BackendUserStatus;
  isEnabled:        boolean;
  roles:            RoleDto[];
  createdOnUtc:     string;
  lastLoginOnUtc:   string | null;
  invitationToken?: string;
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
