import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';

/**
 * Blocks every AppShell route when the signed-in user's current access token carries
 * `mfa_setup_required: true` (an admin has required MFA for this account and it isn't enrolled
 * yet) — redirects to the dedicated forced-enrollment page instead. Runs alongside authGuard on
 * the AppShell parent route so it applies uniformly to every child route, including /security.
 */
export const mfaSetupGuard: CanActivateFn = () => {
  const store  = inject(AuthStore);
  const router = inject(Router);

  return store.currentUser()?.mfaSetupRequired
    ? router.createUrlTree(['/auth/setup-mfa'])
    : true;
};
