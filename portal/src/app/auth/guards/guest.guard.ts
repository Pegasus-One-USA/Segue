import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { AppInitService } from '../../onboarding/services/app-init.service';

/** Prevents authenticated users from accessing guest-only pages (login, signup). */
export const guestGuard: CanActivateFn = () => {
  const store   = inject(AuthStore);
  const router  = inject(Router);
  const appInit = inject(AppInitService);

  if (store.isAuthenticated()) return router.createUrlTree(['/dashboard']);
  // First-run: no users provisioned yet → force the one-time setup screen.
  if (appInit.requiresSetup()) return router.createUrlTree(['/setup']);
  return true;
};
