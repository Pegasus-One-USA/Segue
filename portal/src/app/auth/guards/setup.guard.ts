import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AppInitService } from '../../onboarding/services/app-init.service';

/**
 * Gates the /setup route: allowed only while the deployment still requires first-run setup
 * (no users exist). Once setup is complete, any attempt to reach /setup is redirected to login.
 */
export const setupGuard: CanActivateFn = () => {
  const appInit = inject(AppInitService);
  const router  = inject(Router);
  return appInit.requiresSetup() ? true : router.createUrlTree(['/auth/login']);
};
