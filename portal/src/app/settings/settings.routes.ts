import { Routes } from '@angular/router';
import { authGuard } from '../auth/guards/auth.guard';

export const SETTINGS_ROUTES: Routes = [
  {
    path: 'branding',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/branding/branding-settings.component').then(m => m.BrandingSettingsComponent),
  },
  { path: '', redirectTo: 'branding', pathMatch: 'full' },
];
