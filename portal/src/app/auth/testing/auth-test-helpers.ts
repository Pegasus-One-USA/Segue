import { Permission, Role, User } from '../models/user.model';

/** Minimal valid User for tests — only `permissions`/`role` vary per test case. `roles` defaults to `[]`
 *  (unchanged from before) since most callers don't care about it; pass it explicitly for a test that
 *  needs real Role objects on the session (e.g. cross-referencing by name/isFullAccess). */
export function makeTestUser(permissionNames: string[], role = 'Operations', roles: Role[] = []): User {
  const permissions: Permission[] = permissionNames.map(name => {
    const dot = name.indexOf('.');
    return {
      id: name,
      name,
      displayName: name,
      resource: dot >= 0 ? name.slice(0, dot) : name,
      action: dot >= 0 ? name.slice(dot + 1) : '',
      description: name,
    };
  });

  return {
    id: 'u1', email: 'u1@test.com', firstName: 'Test', lastName: 'User', fullName: 'Test User',
    role, roles, permissions, directPermissionAllocations: [], orgId: 'org1',
    status: 'active', loginType: 'local', mustChangePassword: false,
    emailVerified: true, twoFactorEnabled: false, createdAt: '', updatedAt: '',
  };
}
