import { inject } from '@angular/core';
import { CanActivateFn, ActivatedRouteSnapshot, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { UserRole } from '../models/user.model';

export const roleGuard: CanActivateFn = (route: ActivatedRouteSnapshot) => {
  const auth    = inject(AuthService);
  const router  = inject(Router);
  const allowed = route.data['roles'] as UserRole[] | undefined;
  const user    = auth.currentUser();
  if (!user || (allowed && !allowed.includes(user.role))) {
    return router.createUrlTree(['/unauthorized']);
  }
  return true;
};
