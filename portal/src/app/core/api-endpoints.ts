/**
 * Single source of truth for backend REST endpoint paths.
 * If the API's routes change, update the path here — not in the services that call them.
 */
import { environment } from '../../environments/environment';

export const API_V1_BASE = `${environment.apiBase}/api/v1`;

// ─── Auth (AuthController — api/v1/auth) ────────────────────────────────────────
export const AUTH_ENDPOINTS = {
  login:          `${API_V1_BASE}/auth/internal/login`,
  logout:         `${API_V1_BASE}/auth/logout`,
  me:             `${API_V1_BASE}/auth/me`,
  refresh:        `${API_V1_BASE}/auth/refresh`,
  forgotPassword: `${API_V1_BASE}/auth/internal/forgot-password`,
  resetPassword:  `${API_V1_BASE}/auth/internal/reset-password`,
  changePassword: `${API_V1_BASE}/auth/internal/change-password`,
};

// ─── Users (UsersController — api/v1/users) ─────────────────────────────────────
export const USERS_ENDPOINTS = {
  list:         `${API_V1_BASE}/users`,
  byId:         (id: string) => `${API_V1_BASE}/users/${id}`,
  invite:       `${API_V1_BASE}/users/invite`,
  resendInvite: (id: string) => `${API_V1_BASE}/users/${id}/resend-invite`,
  status:       (id: string) => `${API_V1_BASE}/users/${id}/status`,
  roles:        (id: string) => `${API_V1_BASE}/users/${id}/roles`,
  removeRole:   (id: string, roleId: string) => `${API_V1_BASE}/users/${id}/roles/${roleId}`,
  permissionAllocations:       (id: string) => `${API_V1_BASE}/users/${id}/permission-allocations`,
  permissionAllocationById:    (id: string, permissionId: string) =>
    `${API_V1_BASE}/users/${id}/permission-allocations/${permissionId}`,
};

// ─── Roles & Permissions (RolesController — api/v1/roles, permissions) ─────────
export const ROLES_ENDPOINTS = {
  list: `${API_V1_BASE}/roles`,
  byId: (id: string) => `${API_V1_BASE}/roles/${id}`,
};

export const PERMISSIONS_ENDPOINTS = {
  list: `${API_V1_BASE}/permissions`,
};
