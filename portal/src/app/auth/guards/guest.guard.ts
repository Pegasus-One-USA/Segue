import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';

/** Prevents authenticated users from accessing guest-only pages (login, signup). */
export const guestGuard: CanActivateFn = () => {
  const store  = inject(AuthStore);
  const router = inject(Router);
  return store.isAuthenticated() ? router.createUrlTree(['/dashboard']) : true;
};
