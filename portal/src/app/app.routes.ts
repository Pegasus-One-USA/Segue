import { Routes } from '@angular/router';
import { authGuard } from './auth/guards/auth.guard';
import { permissionGuard } from './auth/guards/permission.guard';
import { setupGuard } from './auth/guards/setup.guard';
import { mfaSetupGuard } from './auth/guards/mfa-setup.guard';
import { unsavedChangesGuard } from './core/guards/unsaved-changes.guard';

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

      // Dashboard is a mandatory platform entry point, not an RBAC-controlled permission — every
      // authenticated role can reach it (the App Shell route above already applies
      // authGuard/mfaSetupGuard to every child here, so this still requires being logged in, just
      // no permission on top). Its stat tiles/"Recent Workflows" widget still call the same
      // /workflow-runs[/stats] endpoints Workflows itself uses, which stay workflow.view-gated —
      // see dashboard.component.ts/pipeline-run.service.ts for how those widgets degrade instead
      // of erroring when that permission is missing.
      {
        path: 'dashboard',
        loadChildren: () =>
          import('./dashboard/dashboard.routes').then(m => m.DASHBOARD_ROUTES),
      },

      // Workflow / Pipeline Builder — gated on workflow.view only (reach/view baseline); the finer
      // create-vs-edit mutation permission depends on the ?id= query param (new vs. existing workflow),
      // which a route guard can't branch on cleanly, so it's enforced inside the component itself
      // (WorkflowBuilderComponent.canMutate) and independently by the backend (workflow.create/edit).
      {
        path: 'workflow-builder',
        canActivate: [permissionGuard],
        // Module access (workflow.view OR any workflow-node permission), not a plain workflow.view
        // check — see permission.guard.ts / PermissionService.hasWorkflowModuleAccess.
        data: { workflowModuleAccess: true },
        canDeactivate: [unsavedChangesGuard],
        loadComponent: () =>
          import('./pages/workflow-builder/workflow-builder.component').then(
            m => m.WorkflowBuilderComponent
          ),
      },
      // V2 — the independent Source → Destination → Mapping → Transformation → De-identification canvas.
      // A fully separate component/service/model tree from V1 above (see pages/workflow-builder-v2/) —
      // nothing here shares code with the V1 route.
      {
        path: 'workflow-builder-v2',
        canActivate: [permissionGuard],
        data: { workflowModuleAccess: true },
        canDeactivate: [unsavedChangesGuard],
        loadComponent: () =>
          import('./pages/workflow-builder-v2/workflow-builder-v2.component').then(
            m => m.WorkflowBuilderV2Component
          ),
      },

      // Workflows list (launch / run / view destination data)
      {
        path: 'workflows',
        canActivate: [permissionGuard],
        data: { workflowModuleAccess: true },
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

      // Organization Settings shell — Branding, EHR Endpoints, Source/Destination Connections,
      // Allowed Origins, System Security now live here as individual tabs. Each child route below
      // carries its own original guard (see settings.routes.ts), so no parent-level gate here.
      {
        path: 'settings',
        loadChildren: () =>
          import('./settings/settings.routes').then(m => m.SETTINGS_ROUTES),
      },

      // Backward-compatible redirects for the old standalone URLs these pages used to live at.
      { path: 'ehr-endpoints', redirectTo: 'settings/ehr-endpoints' },
      { path: 'allowed-origins', redirectTo: 'settings/allowed-origins' },
      { path: 'system-security', redirectTo: 'settings/system-settings/security' },
      { path: 'source-connections', redirectTo: 'settings/workflow-configurations/source-connections' },
      { path: 'destination-connections', redirectTo: 'settings/workflow-configurations/destination-connections' },

      // User Account pages
      {
        path: 'profile',
        loadComponent: () =>
          import('./user/pages/profile/profile.component').then(m => m.ProfileComponent),
      },
      {
        path: 'account-settings',
        canDeactivate: [unsavedChangesGuard],
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
        canDeactivate: [unsavedChangesGuard],
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

      // Execution History (per-route pipeline run history: fetch/map/store detail) — reuses workflow.view,
      // same as Dashboard above (this list is backed by the identical /workflow-runs endpoint).
      {
        path: 'execution-history',
        canActivate: [permissionGuard],
        data: { permissions: ['workflow.view'] },
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

      // Governance shell — Audit Logs, Correlation Search, etc. now live here as tabs (governance-
      // shell.component.ts). Effective URLs are unchanged (still /governance/<page>) since each
      // child's own path segment already carried the 'governance/' prefix before consolidation —
      // it's now supplied by this parent instead. Each child keeps its original guard/permission.
      {
        path: 'governance',
        loadComponent: () =>
          import('./governance/layout/governance-shell.component').then(m => m.GovernanceShellComponent),
        children: [
          {
            path: 'audit-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/audit-logs/audit-logs.component').then(m => m.AuditLogsComponent),
          },
          {
            path: 'authentication-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/authentication-logs/authentication-logs.component').then(
                m => m.AuthenticationLogsComponent
              ),
          },
          // Drill-down only — never had its own sidebar/tab entry, reached via links from other pages.
          {
            path: 'configuration-comparison',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/configuration-comparison/configuration-comparison.component').then(
                m => m.ConfigurationComparisonComponent
              ),
          },
          // Drill-down only — never had its own sidebar/tab entry, reached via links from other pages.
          {
            path: 'data-lineage/:resourceRecordId',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/data-lineage/data-lineage.component').then(m => m.DataLineageComponent),
          },
          {
            path: 'alert-rules',
            canActivate: [permissionGuard],
            canDeactivate: [unsavedChangesGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/alert-rules/alert-rules.component').then(m => m.AlertRulesComponent),
          },
          {
            path: 'alerts',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/alerts/alerts.component').then(m => m.AlertsComponent),
          },
          {
            path: 'authorization-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/authorization-logs/authorization-logs.component').then(
                m => m.AuthorizationLogsComponent
              ),
          },
          {
            path: 'archive',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/archive/archive.component').then(m => m.ArchiveComponent),
          },
          {
            path: 'oauth-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/oauth-logs/oauth-logs.component').then(m => m.OAuthLogsComponent),
          },
          {
            path: 'log-settings',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/log-settings/log-settings.component').then(m => m.LogSettingsComponent),
          },
          {
            path: 'data-access-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/data-access-logs/data-access-logs.component').then(
                m => m.DataAccessLogsComponent
              ),
          },
          {
            path: 'security-events',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/security-events/security-events.component').then(
                m => m.SecurityEventsComponent
              ),
          },
          {
            path: 'retention-policies',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/retention-policies/retention-policies.component').then(
                m => m.RetentionPoliciesComponent
              ),
          },
          {
            path: 'compliance-reports',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/compliance-reports/compliance-reports.component').then(
                m => m.ComplianceReportsComponent
              ),
          },
          {
            path: 'smart-launch-logs',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/smart-launch-logs/smart-launch-logs.component').then(
                m => m.SmartLaunchLogsComponent
              ),
          },
          {
            path: 'correlation-search',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./governance/pages/correlation-search/correlation-search.component').then(
                m => m.CorrelationSearchComponent
              ),
          },
          { path: '', redirectTo: 'audit-logs', pathMatch: 'full' },
        ],
      },

      // Operations shell — System Health, Pipeline Executions, etc. now live here as tabs
      // (operations-shell.component.ts). Each child keeps its original guard/permission.
      {
        path: 'operations',
        loadComponent: () =>
          import('./operations/layout/operations-shell.component').then(m => m.OperationsShellComponent),
        children: [
          {
            path: 'system-health',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/system-health/system-health.component').then(
                m => m.SystemHealthPageComponent
              ),
          },
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
            path: 'queue-monitor',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/queue-monitor/queue-monitor.component').then(m => m.QueueMonitorComponent),
          },
          {
            path: 'api-analytics',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/api-analytics/api-analytics.component').then(m => m.ApiAnalyticsComponent),
          },
          {
            path: 'scheduler-history',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/scheduler-history/scheduler-history.component').then(
                m => m.SchedulerHistoryComponent
              ),
          },
          {
            path: 'retry-history',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/retry-history/retry-history.component').then(m => m.RetryHistoryComponent),
          },
          {
            path: 'errors',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/errors/errors.component').then(m => m.ErrorsComponent),
          },
          {
            path: 'api-requests',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/api-requests/api-requests.component').then(m => m.ApiRequestsComponent),
          },
          {
            path: 'exports',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/exports/exports.component').then(m => m.ExportsComponent),
          },
          {
            path: 'notifications',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/notifications/notifications.component').then(m => m.NotificationsComponent),
          },
          {
            path: 'validation-failures',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/validation-failures/validation-failures.component').then(
                m => m.ValidationFailuresComponent
              ),
          },
          {
            path: 'endpoint-health',
            canActivate: [permissionGuard],
            data: { permissions: ['governance.read'] },
            loadComponent: () =>
              import('./operations/pages/endpoint-health/endpoint-health.component').then(
                m => m.EndpointHealthComponent
              ),
          },
          // System Health and Pipeline Executions are no longer the default landing tab — both are
          // hidden from the menu (see operations-shell.component.ts): System Health only reports one
          // arbitrary container's health under multi-replica deployment, and Pipeline Executions has no
          // authoring UI anywhere in the portal. Errors is the best at-a-glance triage tab, so it leads
          // instead. Routes stay intact, just not the default.
          { path: '', redirectTo: 'errors', pathMatch: 'full' },
        ],
      },

      // Backward-compatible redirects for the old bare (non-operations-prefixed) URLs.
      { path: 'pipeline-executions', redirectTo: 'operations/pipeline-executions' },
      { path: 'pipeline-executions/:id', redirectTo: 'operations/pipeline-executions/:id' },

      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
    ],
  },

  { path: '**', redirectTo: 'auth/login' },
];
