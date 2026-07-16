import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';

// Stricter than permissionGuard's isAdmin() bypass (which also lets a regular Admin through) — for
// screens the backend restricts to SuperAdmin only (e.g. AllowedCorsOriginsController's
// AuthorizationPolicies.SuperAdminOnly), so an Admin doesn't see a route that would just 403.
export const superAdminGuard: CanActivateFn = () => {
  const store  = inject(AuthStore);
  const router = inject(Router);

  if (!store.isAuthenticated()) return router.createUrlTree(['/auth/login']);

  return store.hasRole('SuperAdmin') ? true : router.createUrlTree(['/unauthorized']);
};
