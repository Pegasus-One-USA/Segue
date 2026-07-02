import { inject } from '@angular/core';
import { CanActivateFn, ActivatedRouteSnapshot, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';

export const permissionGuard: CanActivateFn = (route: ActivatedRouteSnapshot) => {
  const store      = inject(AuthStore);
  const router     = inject(Router);
  const required   = route.data['permissions'] as string[] | undefined;
  const requireAll = route.data['requireAll']  as boolean  | undefined;

  if (!store.isAuthenticated()) return router.createUrlTree(['/auth/login']);
  if (!required?.length)        return true;

  const hasAccess = requireAll
    ? required.every(p => store.hasPermission(p))
    : required.some(p  => store.hasPermission(p));

  return hasAccess ? true : router.createUrlTree(['/unauthorized']);
};
