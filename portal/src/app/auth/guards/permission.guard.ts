import { inject } from '@angular/core';
import { CanActivateFn, ActivatedRouteSnapshot, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { PermissionService } from '../services/permission.service';

export const permissionGuard: CanActivateFn = (route: ActivatedRouteSnapshot) => {
  const store       = inject(AuthStore);
  const permissions = inject(PermissionService);
  const router      = inject(Router);
  const required    = route.data['permissions'] as string[] | undefined;
  const requireAll  = route.data['requireAll']  as boolean  | undefined;
  // The Workflow module's "can this role even reach it" rule is workflow.view OR any workflow-node
  // permission — not a plain "holds any of these exact codes" OR-list, so it's its own flag rather than
  // a `permissions` array. See PermissionService.hasWorkflowModuleAccess, the single shared source of
  // truth also used by the sidebar and the Node Library's open-gate.
  const workflowModuleAccess = route.data['workflowModuleAccess'] as boolean | undefined;

  if (!store.isAuthenticated()) return router.createUrlTree(['/auth/login']);

  if (workflowModuleAccess) {
    return permissions.hasWorkflowModuleAccess() ? true : router.createUrlTree(['/unauthorized']);
  }

  if (!required?.length)        return true;

  // SuperAdmin / GlobalAdmin always pass (map to system-admin / tenant-admin here).
  if (store.isAdmin()) return true;

  const hasAccess = requireAll
    ? required.every(p => store.hasPermission(p))
    : required.some(p  => store.hasPermission(p));

  return hasAccess ? true : router.createUrlTree(['/unauthorized']);
};
