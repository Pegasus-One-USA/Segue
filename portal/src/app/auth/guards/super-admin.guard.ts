import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { AuthStore } from '../store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';

// Stricter than permissionGuard's isAdmin() bypass (which also lets a regular Admin through) — for
// screens the backend restricts to SuperAdmin only (e.g. AllowedCorsOriginsController's
// AuthorizationPolicies.SuperAdminOnly), so an Admin doesn't see a route that would just 403.
//
// RBAC Fix 3 (Step 7 audit): SuperAdminOnlyAuthorizationHandler now ALSO succeeds for any role with
// IsFullAccess=true, not just a literal SuperAdmin claim (see UnifiedAdminAuthorizationHandler's
// matching fallback) — a custom "Full System Access" role is a real, supported end state (creatable
// via role-dialog.component.ts's toggle) that the backend authorizes for these exact screens. This
// guard was the one piece never updated to match, silently redirecting such a user to /unauthorized
// even though the backend would let them through. Resolved the same way role-dialog.component.ts's
// own callerHasFullAccess does — fetch the real per-role IsFullAccess flag via IRoleService.getRoles()
// and cross-reference by name against the roles this session's own claims say it holds — never a
// hardcoded role name for this capability. The existing hasRole('SuperAdmin') check stays first and
// short-circuits with zero API calls for the common case (a real SuperAdmin), exactly as before;
// getRoles() is only ever called for a non-SuperAdmin caller, so nothing that already worked pays any
// extra cost. Fails closed: any error resolving the role list (network failure, a non-full-access
// caller lacking role.view so the endpoint itself 403s, etc.) redirects to /unauthorized, same as
// today's plain "not SuperAdmin" outcome — never silently lets a caller through.
export const superAdminGuard: CanActivateFn = () => {
  const store   = inject(AuthStore);
  const router  = inject(Router);
  const roleSvc = inject(IRoleService);

  if (!store.isAuthenticated()) return router.createUrlTree(['/auth/login']);

  if (store.hasRole('SuperAdmin')) return true;

  const heldRoleNames = new Set(store.roles().map(r => r.displayName));

  return roleSvc.getRoles().pipe(
    map(allRoles => allRoles.some(r => heldRoleNames.has(r.name) && r.isFullAccess)
      ? true
      : router.createUrlTree(['/unauthorized'])),
    catchError(() => of(router.createUrlTree(['/unauthorized']))),
  );
};
