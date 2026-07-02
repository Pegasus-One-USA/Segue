import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';

export const authGuard: CanActivateFn = () => {
  const store  = inject(AuthStore);
  const router = inject(Router);
  return store.isAuthenticated() ? true : router.createUrlTree(['/auth/login']);
};
