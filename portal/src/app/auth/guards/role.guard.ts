import { inject } from '@angular/core';
import { CanActivateFn, ActivatedRouteSnapshot, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { UserRole } from '../models/user.model';

export const roleGuard: CanActivateFn = (route: ActivatedRouteSnapshot) => {
  const store   = inject(AuthStore);
  const router  = inject(Router);
  const allowed = route.data['roles'] as UserRole[] | undefined;
  const user    = store.currentUser();

  if (!user) return router.createUrlTree(['/auth/login']);
  if (allowed && !allowed.includes(user.role)) return router.createUrlTree(['/unauthorized']);
  return true;
};
