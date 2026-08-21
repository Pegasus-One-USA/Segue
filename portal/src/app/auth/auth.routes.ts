import { Routes } from '@angular/router';
import { guestGuard } from './guards/guest.guard';
import { authGuard } from './guards/auth.guard';

export const AUTH_ROUTES: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/login/login.component').then(m => m.LoginComponent),
  },
  {
    path: 'signup',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/signup/signup.component').then(m => m.SignupComponent),
  },
  {
    path: 'forgot-password',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/forgot-password/forgot-password.component').then(m => m.ForgotPasswordComponent),
  },
  {
    path: 'reset-password',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/reset-password/reset-password.component').then(m => m.ResetPasswordComponent),
  },
  {
    path: 'magic-link',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/magic-link-request/magic-link-request.component').then(m => m.MagicLinkRequestComponent),
  },
  {
    path: 'magic-link/redeem',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./pages/magic-link-redeem/magic-link-redeem.component').then(m => m.MagicLinkRedeemComponent),
  },
  {
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/change-password/change-password.component').then(m => m.ChangePasswordComponent),
  },
  {
    path: 'setup-mfa',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/mfa-setup-required/mfa-setup-required.component').then(m => m.MfaSetupRequiredComponent),
  },
  {
    path: 'set-password',
    loadComponent: () =>
      import('./pages/set-password/set-password.component').then(m => m.SetPasswordComponent),
  },
  {
    path: 'unauthorized',
    loadComponent: () =>
      import('./pages/unauthorized/unauthorized.component').then(m => m.UnauthorizedComponent),
  },
  { path: '', redirectTo: 'login', pathMatch: 'full' },
];
