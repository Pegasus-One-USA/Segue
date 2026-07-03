import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { AppInitService } from '../../onboarding/services/app-init.service';

export const authGuard: CanActivateFn = () => {
  const store   = inject(AuthStore);
  const router  = inject(Router);
  const appInit = inject(AppInitService);

  // First-run: an un-provisioned deployment (no users yet) is sent to setup, not login.
  if (!store.isAuthenticated() && appInit.requiresSetup()) {
    return router.createUrlTree(['/setup']);
  }
  return store.isAuthenticated() ? true : router.createUrlTree(['/auth/login']);
};
