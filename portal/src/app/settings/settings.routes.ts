import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { superAdminGuard } from '../auth/guards/super-admin.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';

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
        canDeactivate: [unsavedChangesGuard],
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
        // Merges the formerly-standalone Source Connections, Destination Connections, and Mapping
        // Profiles tabs into one screen with a section per former tab — grouped because all three
        // configure the data a workflow moves through (source -> mapping -> destination), not because
        // they share a permission model (Source Connections alone allows sourceconnections.view without
        // configuration.write, so the per-section guards below stay split rather than collapsing to one).
        path: 'workflow-configurations',
        canActivate: [permissionGuard],
        data: { permissions: ['sourceconnections.view', 'configuration.write'] },
        loadComponent: () =>
          import('./layout/workflow-configurations-shell/workflow-configurations-shell.component').then(
            m => m.WorkflowConfigurationsShellComponent
          ),
        children: [
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
            path: 'mapping-profiles',
            canActivate: [permissionGuard],
            data: { permissions: ['configuration.write'] },
            loadComponent: () =>
              import('../mapping-profiles/pages/mapping-profile-list/mapping-profile-list.component').then(
                m => m.MappingProfileListComponent
              ),
          },
          { path: '', redirectTo: 'source-connections', pathMatch: 'full' },
        ],
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
        // Merges the formerly-standalone Email Settings, System Settings, and System Security tabs into
        // one SuperAdmin-gated screen with a section per former tab — all three shared no permission
        // model uniform enough to keep as separate top-level tabs (Email only needed configuration.write),
        // so the merged screen takes the stricter superAdminGuard and Email moves under it.
        path: 'system-settings',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('./layout/system-settings-shell/system-settings-shell.component').then(
            m => m.SystemSettingsShellComponent
          ),
        children: [
          {
            path: 'email',
            canDeactivate: [unsavedChangesGuard],
            loadComponent: () =>
              import('./pages/email-settings/email-settings.component').then(m => m.EmailSettingsComponent),
          },
          {
            path: 'general',
            loadComponent: () =>
              import('../system-settings/pages/system-setting-list/system-setting-list.component').then(
                m => m.SystemSettingListComponent
              ),
          },
          {
            path: 'security',
            loadComponent: () =>
              import('../system-security/pages/app-secret-list/app-secret-list.component').then(
                m => m.AppSecretListComponent
              ),
          },
          { path: '', redirectTo: 'email', pathMatch: 'full' },
        ],
      },
      { path: '', redirectTo: 'branding', pathMatch: 'full' },
    ],
  },
];
