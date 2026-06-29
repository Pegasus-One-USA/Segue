import { Routes } from '@angular/router';

export const USER_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/profile/profile.component').then(m => m.ProfileComponent),
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./pages/account-settings/account-settings.component').then(
        m => m.AccountSettingsComponent
      ),
  },
  {
    path: 'security',
    loadComponent: () =>
      import('./pages/security/security.component').then(m => m.SecurityComponent),
  },
  {
    path: 'preferences',
    loadComponent: () =>
      import('./pages/preferences/preferences.component').then(m => m.PreferencesComponent),
  },
  {
    path: 'help',
    loadComponent: () =>
      import('./pages/help/help.component').then(m => m.HelpComponent),
  },
];
