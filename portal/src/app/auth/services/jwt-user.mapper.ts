import { User, Role, Permission } from '../models/user.model';
import { toUserRole } from './api-user.service';
import { AuthProfileDto } from './auth-profile.model';

/**
 * Builds the portal `User` (roles + permissions) from the decoded FHIRBridge access-token payload.
 * The backend JWT carries `roles` (name(s)) and `permissions` (dotted codes) claims — client-side
 * gating (`AuthStore.hasPermission`) keys off `permission.name`, which we set to the backend code verbatim.
 */

const EMAIL_URI = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress';
const NAMEID_URI = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier';

function toArray(value: unknown): string[] {
  if (value === null || value === undefined) return [];
  return Array.isArray(value) ? value.map(String) : [String(value)];
}

export function buildUserFromJwt(payload: Record<string, unknown>): User {
  const email = String(payload['preferred_username'] ?? payload[EMAIL_URI] ?? payload['email'] ?? '');
  const id = String(payload['oid'] ?? payload[NAMEID_URI] ?? email);
  const displayName = String(payload['name'] ?? email);

  const permissions: Permission[] = toArray(payload['permissions']).map(code => {
    const dot = code.indexOf('.');
    const resource = dot >= 0 ? code.slice(0, dot) : code;
    const action = dot >= 0 ? code.slice(dot + 1) : '';
    return { id: code, name: code, displayName: code, resource, action, description: code };
  });

  const roles: Role[] = toArray(payload['roles']).map(rn => ({
    id: rn,
    name: toUserRole(rn),
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
    role: roles.length ? roles[0].name : 'Audit',
    roles,
    permissions,
    // The JWT's permissions claim is already the backend's merged (role ∪ overrides) set — there's
    // no separate override list to carry here.
    directPermissionAllocations: [],
    // buildUserFromJwt is unused dead code today (confirmed: no live call site) — HIPAA #7 moved every
    // real login/session path to buildUserFromProfile (below), which now carries the real, DB-sourced
    // tenantId. There is no tenant claim on the raw JWT itself (deliberately — see JwtAccessTokenIssuer/
    // ICurrentTenantResolver's remarks), so this placeholder is left as-is rather than wired to a
    // nonexistent claim; if this function is ever revived, it needs a real tenant source first.
    orgId: 'org',
    status: 'active',
    loginType: 'local',
    mustChangePassword: false,
    // Unlike mustChangePassword above (not currently claim-driven), this one has to be read for
    // real — mfaSetupGuard checks it on every navigation, not just right after login.
    mfaSetupRequired: String(payload['mfa_setup_required']) === 'true',
    emailVerified: true,
    twoFactorEnabled: false,
    createdAt: nowIso,
    updatedAt: nowIso,
  };
}

/**
 * HIPAA #7: builds the portal `User` from the backend's UserProfileDto instead of decoding the access
 * token — the token now lives in an HttpOnly cookie this client can't read. `mfaSetupRequired` isn't on
 * the profile DTO (it's still a token-only claim the server enforces via the session-gate middleware, not
 * something the client needs to independently re-derive), so it defaults false here; callers that need it
 * pass it in explicitly (mirrors how `mustChangePassword` is set by the caller after this returns).
 */
export function buildUserFromProfile(profile: AuthProfileDto): User {
  const email = profile.email ?? '';
  const id = profile.userId || profile.externalUserId;
  const displayName = profile.displayName ?? email;

  const permissionCodes = profile.permissions ?? [];
  const permissions: Permission[] = permissionCodes.map(code => {
    const dot = code.indexOf('.');
    const resource = dot >= 0 ? code.slice(0, dot) : code;
    const action = dot >= 0 ? code.slice(dot + 1) : '';
    return { id: code, name: code, displayName: code, resource, action, description: code };
  });

  const roles: Role[] = profile.claimRoles.map(rn => ({
    id: rn,
    name: toUserRole(rn),
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
    role: roles.length ? roles[0].name : 'Audit',
    roles,
    permissions,
    directPermissionAllocations: [],
    // Real, DB-sourced tenant id (see UserProfileDto.TenantId) — was hardcoded to the literal string
    // 'org' for every user before real Tenant support existed; every user always has one now.
    orgId: profile.tenantId ?? '',
    status: 'active',
    loginType: 'local',
    mustChangePassword: profile.requiresPasswordChange ?? false,
    mfaSetupRequired: profile.requiresMfaSetup ?? false,
    emailVerified: true,
    twoFactorEnabled: false,
    // Real values from UserProfileDto.CreatedOnUtc/LastLoginOnUtc — previously createdAt was hardcoded to
    // "now" (so Member Since always showed today's date for every user) and lastLoginAt was never set at
    // all (so Last Login always showed blank). nowIso stays only as a defensive fallback if the backend
    // ever omits createdOnUtc; lastLoginOnUtc is genuinely null for a user who has never logged in before.
    createdAt: profile.createdOnUtc ?? nowIso,
    lastLoginAt: profile.lastLoginOnUtc ?? undefined,
    updatedAt: nowIso,
  };
}
