import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { superAdminGuard } from '../auth/guards/super-admin.guard';

// Every child below keeps the exact guard/permission it had as a standalone top-level route
// before consolidation under this shell — see docs/backend/12-provider-standalone-ehr-launch-fixes.md.
export const SETTINGS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./layout/settings-shell.component').then(m => m.SettingsShellComponent),
    children: [
      {
        path: 'branding',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('./pages/branding/branding-settings.component').then(m => m.BrandingSettingsComponent),
      },
      {
        path: 'ehr-endpoints',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('../ehr-endpoints/pages/ehr-endpoint-list/ehr-endpoint-list.component').then(
            m => m.EhrEndpointListComponent
          ),
      },
      {
        path: 'source-connections',
        canActivate: [permissionGuard],
        data: { permissions: ['sourceconnections.view'] },
        loadComponent: () =>
          import('../source-connections/pages/source-connection-list/source-connection-list.component').then(
            m => m.SourceConnectionListComponent
          ),
      },
      {
        path: 'destination-connections',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('../destination-connections/pages/destination-connection-list/destination-connection-list.component').then(
            m => m.DestinationConnectionListComponent
          ),
      },
      {
        path: 'allowed-origins',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('../allowed-origins/pages/allowed-cors-origin-list/allowed-cors-origin-list.component').then(
            m => m.AllowedCorsOriginListComponent
          ),
      },
      {
        path: 'system-security',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('../system-security/pages/app-secret-list/app-secret-list.component').then(
            m => m.AppSecretListComponent
          ),
      },
      { path: '', redirectTo: 'branding', pathMatch: 'full' },
    ],
  },
];
