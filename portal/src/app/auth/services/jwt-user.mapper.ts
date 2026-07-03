import { User, UserRole, Role, Permission } from '../models/user.model';

/**
 * Builds the portal `User` (roles + permissions) from the decoded FHIRBridge access-token payload.
 * The backend JWT carries `roles` (name(s)) and `permissions` (dotted codes) claims — client-side
 * gating (`AuthStore.hasPermission`) keys off `permission.name`, which we set to the backend code verbatim.
 */

// Backend role name (case-insensitive) → portal UserRole union. Unknown roles fall back to 'viewer'.
const ROLE_MAP: Record<string, UserRole> = {
  superadmin: 'system-admin',
  admin: 'tenant-admin',
  operations: 'pipeline-editor',
  pipelineengineer: 'pipeline-editor',
  developer: 'developer',
  reviewer: 'reviewer',
  analyst: 'analyst',
  auditor: 'auditor',
  viewer: 'viewer',
};

const EMAIL_URI = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress';
const NAMEID_URI = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier';

function toArray(value: unknown): string[] {
  if (value === null || value === undefined) return [];
  return Array.isArray(value) ? value.map(String) : [String(value)];
}

function mapRoleName(name: string): UserRole {
  return ROLE_MAP[name.toLowerCase()] ?? 'viewer';
}

export function buildUserFromJwt(payload: Record<string, unknown>): User {
  const email = String(payload['preferred_username'] ?? payload[EMAIL_URI] ?? payload['email'] ?? '');
  const id = String(payload['oid'] ?? payload[NAMEID_URI] ?? email);
  const displayName = String(payload['name'] ?? email);

  const permissions: Permission[] = toArray(payload['permissions']).map(code => {
    const dot = code.indexOf('.');
    const resource = dot >= 0 ? code.slice(0, dot) : code;
    const action = dot >= 0 ? code.slice(dot + 1) : '';
    return { id: code, name: code, resource, action, description: code };
  });

  const roles: Role[] = toArray(payload['roles']).map(rn => ({
    id: rn,
    name: mapRoleName(rn),
    displayName: rn,
    description: rn,
    permissions,
    color: '#64748b',
    isSystemRole: true,
    createdAt: new Date(0).toISOString(),
  }));

  const [firstName, ...rest] = displayName.split(' ');
  const nowIso = new Date().toISOString();

  return {
    id,
    email,
    firstName: firstName ?? '',
    lastName: rest.join(' '),
    fullName: displayName,
    role: roles.length ? roles[0].name : 'viewer',
    roles,
    permissions,
    orgId: 'org',
    status: 'active',
    loginType: 'local',
    mustChangePassword: false,
    emailVerified: true,
    twoFactorEnabled: false,
    createdAt: nowIso,
    updatedAt: nowIso,
  };
}
