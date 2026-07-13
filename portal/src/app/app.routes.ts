import { Routes } from '@angular/router';
import { authGuard } from './auth/guards/auth.guard';
import { permissionGuard } from './auth/guards/permission.guard';
import { setupGuard } from './auth/guards/setup.guard';
import { mfaSetupGuard } from './auth/guards/mfa-setup.guard';

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
    canActivate: [authGuard, mfaSetupGuard],
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

      // Workflows list (launch / run / view destination data)
      {
        path: 'workflows',
        loadComponent: () =>
          import('./pages/workflow-list/workflow-list.component').then(
            m => m.WorkflowListComponent
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

      // EHR Endpoints directory (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'ehr-endpoints',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('./ehr-endpoints/pages/ehr-endpoint-list/ehr-endpoint-list.component').then(
            m => m.EhrEndpointListComponent
          ),
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

      // Execution History (per-route pipeline run history: fetch/map/store detail)
      {
        path: 'execution-history',
        canActivate: [authGuard],
        loadComponent: () =>
          import('./execution-history/pages/execution-history-list/execution-history-list.component').then(
            m => m.ExecutionHistoryListComponent
          ),
      },
      {
        path: 'execution-history/:id',
        canActivate: [authGuard],
        loadComponent: () =>
          import('./execution-history/pages/execution-history-detail/execution-history-detail.component').then(
            m => m.ExecutionHistoryDetailComponent
          ),
      },

      // ── Governance (permission-gated; SuperAdmin / GlobalAdmin fall through) ──

      // Activity feed list (UserActivityAuditLog — who did what, when, from where)
      {
        path: 'activity',
        canActivate: [permissionGuard],
        data: { permissions: ['auditlogs.read'] },
        loadComponent: () =>
          import('./activity/pages/activity-list/activity-list.component').then(
            m => m.ActivityListComponent
          ),
      },

      // Operational logs (OperationalAuditLog — pipeline/system events)
      {
        path: 'operational-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['auditlogs.read'] },
        loadComponent: () =>
          import('./operational-logs/pages/operational-logs-list/operational-logs-list.component').then(
            m => m.OperationalLogsListComponent
          ),
      },

      // Lineage (ResourceLineageEntry — PHI-free chain of custody)
      {
        path: 'lineage',
        canActivate: [permissionGuard],
        data: { permissions: ['auditlogs.read'] },
        loadComponent: () =>
          import('./lineage/pages/lineage-list/lineage-list.component').then(
            m => m.LineageListComponent
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
