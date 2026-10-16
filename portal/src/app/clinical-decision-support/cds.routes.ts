import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';

// Clinical Decision Support (CDS Hooks). The parent route in app.routes.ts already requires cdshooks.view; each
// child repeats its own gate so the tab strip (cds-nav.ts) and the routes stay in step.
export const CDS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./layout/cds-shell/cds-shell.component').then(m => m.CdsShellComponent),
    children: [
      {
        path: 'services',
        canActivate: [permissionGuard],
        data: { permissions: ['cdshooks.view'] },
        loadComponent: () =>
          import('./pages/cds-service-list/cds-service-list.component').then(m => m.CdsServiceListComponent),
      },
      {
        path: 'services/new',
        canActivate: [permissionGuard],
        canDeactivate: [unsavedChangesGuard],
        data: { permissions: ['cdshooks.view', 'cdshooks.create'], requireAll: true },
        loadComponent: () =>
          import('./pages/cds-service-setup/cds-service-setup.component').then(m => m.CdsServiceSetupComponent),
      },
      {
        path: 'services/:id',
        canActivate: [permissionGuard],
        canDeactivate: [unsavedChangesGuard],
        data: { permissions: ['cdshooks.view'] },
        loadComponent: () =>
          import('./pages/cds-service-setup/cds-service-setup.component').then(m => m.CdsServiceSetupComponent),
      },
      {
        path: 'connect',
        canActivate: [permissionGuard],
        data: { permissions: ['cdshooks.view'] },
        loadComponent: () =>
          import('./pages/cds-connect-ehr/cds-connect-ehr.component').then(m => m.CdsConnectEhrComponent),
      },
      {
        path: 'test',
        canActivate: [permissionGuard],
        data: { permissions: ['cdshooks.view', 'cdshooks.test'], requireAll: true },
        loadComponent: () =>
          import('./pages/cds-test/cds-test.component').then(m => m.CdsTestComponent),
      },
      {
        path: 'activity',
        canActivate: [permissionGuard],
        data: { permissions: ['cdshooks.view'] },
        loadComponent: () =>
          import('./pages/cds-activity/cds-activity.component').then(m => m.CdsActivityComponent),
      },
      { path: '', redirectTo: 'services', pathMatch: 'full' },
    ],
  },
];
