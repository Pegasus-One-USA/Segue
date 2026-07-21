import { Routes } from '@angular/router';
import { authGuard } from './auth/guards/auth.guard';
import { permissionGuard } from './auth/guards/permission.guard';
import { superAdminGuard } from './auth/guards/super-admin.guard';
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

      // Allowed CORS origins (SuperAdmin only — backend enforces AuthorizationPolicies.SuperAdminOnly,
      // stricter than permissionGuard's isAdmin() bypass which also lets a regular Admin through)
      {
        path: 'allowed-origins',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('./allowed-origins/pages/allowed-cors-origin-list/allowed-cors-origin-list.component').then(
            m => m.AllowedCorsOriginListComponent
          ),
      },

      // App-level signing secrets (SuperAdmin only — same policy as Allowed Origins above)
      {
        path: 'system-security',
        canActivate: [superAdminGuard],
        loadComponent: () =>
          import('./system-security/pages/app-secret-list/app-secret-list.component').then(
            m => m.AppSecretListComponent
          ),
      },

      // Source Connections directory (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'source-connections',
        canActivate: [permissionGuard],
        data: { permissions: ['sourceconnections.view'] },
        loadComponent: () =>
          import('./source-connections/pages/source-connection-list/source-connection-list.component').then(
            m => m.SourceConnectionListComponent
          ),
      },

      // Destination Connections directory (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'destination-connections',
        canActivate: [permissionGuard],
        data: { permissions: ['configuration.write'] },
        loadComponent: () =>
          import('./destination-connections/pages/destination-connection-list/destination-connection-list.component').then(
            m => m.DestinationConnectionListComponent
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

      // Governance (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'governance/audit-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/audit-logs/audit-logs.component').then(
            m => m.AuditLogsComponent
          ),
      },
      {
        path: 'governance/authentication-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/authentication-logs/authentication-logs.component').then(
            m => m.AuthenticationLogsComponent
          ),
      },
      {
        path: 'governance/configuration-comparison',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/configuration-comparison/configuration-comparison.component').then(
            m => m.ConfigurationComparisonComponent
          ),
      },
      {
        path: 'governance/data-lineage/:resourceRecordId',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/data-lineage/data-lineage.component').then(
            m => m.DataLineageComponent
          ),
      },
      {
        path: 'governance/alert-rules',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/alert-rules/alert-rules.component').then(
            m => m.AlertRulesComponent
          ),
      },
      {
        path: 'governance/alerts',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/alerts/alerts.component').then(
            m => m.AlertsComponent
          ),
      },
      {
        path: 'governance/authorization-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/authorization-logs/authorization-logs.component').then(
            m => m.AuthorizationLogsComponent
          ),
      },
      {
        path: 'governance/archive',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/archive/archive.component').then(
            m => m.ArchiveComponent
          ),
      },
      {
        path: 'governance/oauth-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/oauth-logs/oauth-logs.component').then(
            m => m.OAuthLogsComponent
          ),
      },
      {
        path: 'governance/log-settings',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/log-settings/log-settings.component').then(
            m => m.LogSettingsComponent
          ),
      },
      {
        path: 'governance/data-access-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/data-access-logs/data-access-logs.component').then(
            m => m.DataAccessLogsComponent
          ),
      },
      {
        path: 'governance/security-events',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/security-events/security-events.component').then(
            m => m.SecurityEventsComponent
          ),
      },

      // Operations (permission-gated; SuperAdmin / GlobalAdmin fall through)
      {
        path: 'pipeline-executions',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./pipeline-executions/pages/pipeline-execution-list/pipeline-execution-list.component').then(
            m => m.PipelineExecutionListComponent
          ),
      },
      {
        path: 'pipeline-executions/:id',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./pipeline-executions/pages/pipeline-execution-detail/pipeline-execution-detail.component').then(
            m => m.PipelineExecutionDetailComponent
          ),
      },
      {
        path: 'operations/queue-monitor',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/queue-monitor/queue-monitor.component').then(
            m => m.QueueMonitorComponent
          ),
      },
      {
        path: 'operations/api-analytics',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/api-analytics/api-analytics.component').then(
            m => m.ApiAnalyticsComponent
          ),
      },
      {
        path: 'operations/system-health',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/system-health/system-health.component').then(
            m => m.SystemHealthPageComponent
          ),
      },
      {
        path: 'operations/scheduler-history',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/scheduler-history/scheduler-history.component').then(
            m => m.SchedulerHistoryComponent
          ),
      },
      {
        path: 'operations/retry-history',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/retry-history/retry-history.component').then(
            m => m.RetryHistoryComponent
          ),
      },
      {
        path: 'operations/errors',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/errors/errors.component').then(
            m => m.ErrorsComponent
          ),
      },
      {
        path: 'operations/api-requests',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/api-requests/api-requests.component').then(
            m => m.ApiRequestsComponent
          ),
      },
      {
        path: 'operations/exports',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/exports/exports.component').then(
            m => m.ExportsComponent
          ),
      },
      {
        path: 'operations/notifications',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/notifications/notifications.component').then(
            m => m.NotificationsComponent
          ),
      },
      {
        path: 'operations/validation-failures',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/validation-failures/validation-failures.component').then(
            m => m.ValidationFailuresComponent
          ),
      },
      {
        path: 'operations/endpoint-health',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./operations/pages/endpoint-health/endpoint-health.component').then(
            m => m.EndpointHealthComponent
          ),
      },
      {
        path: 'governance/retention-policies',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/retention-policies/retention-policies.component').then(
            m => m.RetentionPoliciesComponent
          ),
      },
      {
        path: 'governance/compliance-reports',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/compliance-reports/compliance-reports.component').then(
            m => m.ComplianceReportsComponent
          ),
      },
      {
        path: 'governance/smart-launch-logs',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/smart-launch-logs/smart-launch-logs.component').then(
            m => m.SmartLaunchLogsComponent
          ),
      },
      {
        path: 'governance/correlation-search',
        canActivate: [permissionGuard],
        data: { permissions: ['governance.read'] },
        loadComponent: () =>
          import('./governance/pages/correlation-search/correlation-search.component').then(
            m => m.CorrelationSearchComponent
          ),
      },

      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
    ],
  },

  { path: '**', redirectTo: 'auth/login' },
];
