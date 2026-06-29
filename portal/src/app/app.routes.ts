import { Routes } from '@angular/router';
import { authGuard } from './auth/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'auth',
    loadChildren: () => import('./auth/auth.routes').then(m => m.AUTH_ROUTES),
  },
  {
    path: 'dashboard',
    canActivate: [authGuard],
    loadChildren: () => import('./dashboard/dashboard.routes').then(m => m.DASHBOARD_ROUTES),
  },
  {
    path: 'workflow-builder',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/workflow-builder/workflow-builder.component').then(
        m => m.WorkflowBuilderComponent
      ),
  },
  // ── User Management (admin page) ─────────────────────────────────
  {
    path: 'user-management',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/user-management/user-management.component').then(
        m => m.UserManagementComponent
      ),
  },
  // ── User Account Management ──────────────────────────────────────
  {
    path: 'profile',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./user/pages/profile/profile.component').then(m => m.ProfileComponent),
  },
  {
    path: 'account-settings',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./user/pages/account-settings/account-settings.component').then(
        m => m.AccountSettingsComponent
      ),
  },
  {
    path: 'security',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./user/pages/security/security.component').then(m => m.SecurityComponent),
  },
  {
    path: 'preferences',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./user/pages/preferences/preferences.component').then(m => m.PreferencesComponent),
  },
  {
    path: 'help',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./user/pages/help/help.component').then(m => m.HelpComponent),
  },
  { path: '',   redirectTo: 'dashboard', pathMatch: 'full' },
  { path: '**', redirectTo: 'auth/login' },
];
