import { Routes } from '@angular/router';
import { authGuard } from './auth/guards/auth.guard';
import { permissionGuard } from './auth/guards/permission.guard';
import { setupGuard } from './auth/guards/setup.guard';

export const routes: Routes = [

  // ── First-run setup (no shell) — MUST precede the AppShell route ─────────────
  {
    path: 'setup',
    canActivate: [setupGuard],
    loadComponent: () =>
      import('./onboarding/pages/setup-super-admin/setup-super-admin.component').then(
        m => m.SetupSuperAdminComponent
      ),
  },

  // ── Auth (no shell) ─────────────────────────────────────────────────────────
  {
    path: 'auth',
    loadChildren: () => import('./auth/auth.routes').then(m => m.AUTH_ROUTES),
  },

  // ── Unauthorized (no shell) ─────────────────────────────────────────────────
  {
    path: 'unauthorized',
    loadComponent: () =>
      import('./auth/pages/unauthorized/unauthorized.component').then(
        m => m.UnauthorizedComponent
      ),
  },

  // ── App Shell — wraps every authenticated page ──────────────────────────────
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./layout/app-shell/app-shell.component').then(m => m.AppShellComponent),
    children: [

      // Dashboard
      {
        path: 'dashboard',
        loadChildren: () =>
          import('./dashboard/dashboard.routes').then(m => m.DASHBOARD_ROUTES),
      },

      // Workflow / Pipeline Builder
      {
        path: 'workflow-builder',
        loadComponent: () =>
          import('./pages/workflow-builder/workflow-builder.component').then(
            m => m.WorkflowBuilderComponent
          ),
      },

      // User Management (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'user-management',
        canActivate: [permissionGuard],
        data: { permissions: ['user.view'] },
        loadChildren: () =>
          import('./user-management/user-management.routes').then(
            m => m.USER_MANAGEMENT_ROUTES
          ),
      },

      // Organization Settings — e.g. Branding (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'settings',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadChildren: () =>
          import('./settings/settings.routes').then(m => m.SETTINGS_ROUTES),
      },

      // User Account pages
      {
        path: 'profile',
        loadComponent: () =>
          import('./user/pages/profile/profile.component').then(m => m.ProfileComponent),
      },
      {
        path: 'account-settings',
        loadComponent: () =>
          import('./user/pages/account-settings/account-settings.component').then(
            m => m.AccountSettingsComponent
          ),
      },
      {
        path: 'security',
        loadComponent: () =>
          import('./user/pages/security/security.component').then(m => m.SecurityComponent),
      },
      {
        path: 'preferences',
        loadComponent: () =>
          import('./user/pages/preferences/preferences.component').then(
            m => m.PreferencesComponent
          ),
      },
      {
        path: 'help',
        loadComponent: () =>
          import('./user/pages/help/help.component').then(m => m.HelpComponent),
      },

      // Pipelines list
      {
        path: 'pipelines',
        canActivate: [authGuard],
        loadComponent: () =>
          import('./pipelines/pages/pipeline-list/pipeline-list.component').then(
            m => m.PipelineListComponent
          ),
      },

      // Activity feed list
      {
        path: 'activity',
        canActivate: [authGuard],
        loadComponent: () =>
          import('./activity/pages/activity-list/activity-list.component').then(
            m => m.ActivityListComponent
          ),
      },

      // Execution Logs
      {
        path: 'logs',
        loadComponent: () =>
          import('./pages/execution-logs/execution-logs.component').then(
            m => m.ExecutionLogsComponent
          ),
      },

      // Schedules
      {
        path: 'schedules',
        loadComponent: () =>
          import('./pages/schedules/schedules.component').then(
            m => m.SchedulesComponent
          ),
      },

      // Reports
      {
        path: 'reports',
        loadComponent: () =>
          import('./pages/reports/reports.component').then(
            m => m.ReportsComponent
          ),
      },

      // Configuration
      {
        path: 'config',
        loadComponent: () =>
          import('./pages/configuration/configuration.component').then(
            m => m.ConfigurationComponent
          ),
      },

      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
    ],
  },

  { path: '**', redirectTo: 'auth/login' },
];
