import { Component, input, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthStore } from '../../../auth/store/auth.store';
import { BrandingService } from '../../../services/branding.service';

interface NavItem {
  type: 'item';
  icon: string;
  label: string;
  route: string;
  exact?: boolean;
  /** Omit for entries every authenticated user may see; otherwise hidden unless the user has one of these. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — stricter than `permissions`, which a regular Admin
   *  also satisfies via isAdmin(). Takes precedence over `permissions` when both are set. */
  superAdminOnly?: boolean;
}

interface NavSection {
  type: 'section';
  label: string;
}

type NavEntry = NavItem | NavSection;

const NAV_ENTRIES: NavEntry[] = [
  { type: 'item', icon: '⊞',  label: 'Dashboard',        route: '/dashboard' },
  { type: 'item', icon: '🔐', label: 'Role',             route: '/user-management/roles', permissions: ['role.view'] },
  { type: 'item', icon: '👥', label: 'User Management',  route: '/user-management',          exact: true, permissions: ['user.view'] },
  { type: 'item', icon: '🗂', label: 'Workflows',        route: '/workflows' },
  { type: 'item', icon: '▶',  label: 'Execution History', route: '/execution-history' },
  { type: 'item', icon: '⚙',  label: 'Settings',         route: '/settings/branding', permissions: ['configuration.write'] },
  { type: 'item', icon: '🏥', label: 'EHR Endpoints',    route: '/ehr-endpoints',     permissions: ['configuration.write'] },
  { type: 'item', icon: '🔌', label: 'Source Connections', route: '/source-connections', permissions: ['sourceconnections.view'] },
  { type: 'item', icon: '🔌', label: 'Destination Connections', route: '/destination-connections', permissions: ['configuration.write'] },
  { type: 'item', icon: '🌐', label: 'Allowed Origins',    route: '/allowed-origins',   superAdminOnly: true },

  { type: 'section', label: 'Operations' },
  { type: 'item', icon: '🧬', label: 'Pipeline Executions',  route: '/pipeline-executions',           permissions: ['governance.read'] },
  { type: 'item', icon: '📬', label: 'Queue Monitor',        route: '/operations/queue-monitor',      permissions: ['governance.read'] },
  { type: 'item', icon: '📈', label: 'API Analytics',        route: '/operations/api-analytics',      permissions: ['governance.read'] },
  { type: 'item', icon: '💻', label: 'System Health',        route: '/operations/system-health',      permissions: ['governance.read'] },
  { type: 'item', icon: '⏱', label: 'Scheduler History',   route: '/operations/scheduler-history', permissions: ['governance.read'] },
  { type: 'item', icon: '🔁', label: 'Retry History',       route: '/operations/retry-history',     permissions: ['governance.read'] },
  { type: 'item', icon: '⚠',  label: 'Errors',              route: '/operations/errors',            permissions: ['governance.read'] },
  { type: 'item', icon: '🌐', label: 'API Requests',        route: '/operations/api-requests',      permissions: ['governance.read'] },
  { type: 'item', icon: '📤', label: 'Exports',             route: '/operations/exports',           permissions: ['governance.read'] },
  { type: 'item', icon: '✉',  label: 'Notifications',       route: '/operations/notifications',     permissions: ['governance.read'] },
  { type: 'item', icon: '⚡',  label: 'Validation Failures',  route: '/operations/validation-failures', permissions: ['governance.read'] },
  { type: 'item', icon: '📡', label: 'Endpoint Health',      route: '/operations/endpoint-health',   permissions: ['governance.read'] },

  { type: 'section', label: 'Governance' },
  { type: 'item', icon: '🔎', label: 'Correlation Search',  route: '/governance/correlation-search',  permissions: ['governance.read'] },
  { type: 'item', icon: '📜', label: 'Audit Logs',          route: '/governance/audit-logs',          permissions: ['governance.read'] },
  { type: 'item', icon: '🔑', label: 'Authentication Logs', route: '/governance/authentication-logs', permissions: ['governance.read'] },
  { type: 'item', icon: '🚀', label: 'SMART Launch Logs',    route: '/governance/smart-launch-logs',    permissions: ['governance.read'] },
  { type: 'item', icon: '🔁', label: 'OAuth',                route: '/governance/oauth-logs',           permissions: ['governance.read'] },
  { type: 'item', icon: '⛔', label: 'Authorization Logs',   route: '/governance/authorization-logs',  permissions: ['governance.read'] },
  { type: 'item', icon: '🩺', label: 'Data Access Logs',    route: '/governance/data-access-logs',    permissions: ['governance.read'] },
  { type: 'item', icon: '🛡', label: 'Security Events',     route: '/governance/security-events',     permissions: ['governance.read'] },
  { type: 'item', icon: '📊', label: 'Compliance Reports',  route: '/governance/compliance-reports',  permissions: ['governance.read'] },
  { type: 'item', icon: '🗄', label: 'Retention Policies',  route: '/governance/retention-policies',  permissions: ['governance.read'] },
  { type: 'item', icon: '📦', label: 'Archive',             route: '/governance/archive',             permissions: ['governance.read'] },
  { type: 'item', icon: '🛠', label: 'Log Settings',        route: '/governance/log-settings',        permissions: ['governance.read'] },
  { type: 'item', icon: '🔔', label: 'Alerts',              route: '/governance/alerts',               permissions: ['governance.read'] },
  { type: 'item', icon: '⚙️', label: 'Alert Rules',         route: '/governance/alert-rules',          permissions: ['governance.read'] },
];

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
  readonly collapsed = input(false);

  private readonly store = inject(AuthStore);
  protected readonly branding = inject(BrandingService);

  // Same visibility rule as permissionGuard: admins always pass; otherwise the item needs at
  // least one of its required permissions (no `permissions` = visible to everyone logged in).
  readonly navEntries = computed<NavEntry[]>(() =>
    NAV_ENTRIES.filter(entry => {
      if (this.isSection(entry)) return true;
      if (entry.superAdminOnly) return this.store.hasRole('SuperAdmin');
      if (!entry.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return entry.permissions.some(p => this.store.hasPermission(p));
    })
  );

  isItem(entry: NavEntry): entry is NavItem {
    return entry.type === 'item';
  }

  isSection(entry: NavEntry): entry is NavSection {
    return entry.type === 'section';
  }
}
