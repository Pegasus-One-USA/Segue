import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';

interface GovernanceTab {
  label: string;
  route: string;
  icon: string;
  permissions?: string[];
  /** Hidden from the tab menu. OAuth is a pure filtered subset of Authentication Logs (same table,
   *  AuthenticationType starting with "OAuth"), carrying no data the Authentication Logs tab doesn't
   *  already have. Archive is hidden per user direction — its restore action is a 501 stub, so surfacing
   *  the tab invites a "why doesn't restore work" support ticket before that's implemented. Retention
   *  Policies and Log Settings are hidden per user direction too, as are Alerts, Alert Rules, and
   *  Data Access Logs. Audit Logs, Authentication Logs, SMART Launch Logs, Authorization Logs, and
   *  Security Events are hidden per user direction as well, since every one of them is fully searchable
   *  by Correlation ID via the Correlation Search tab — Compliance Reports stays visible because it's a
   *  PDF export across a date range, not a per-row log, so Correlation Search has no equivalent for it.
   *  Route and component are kept intact for all of these; only the menu entry is suppressed. */
  hidden?: boolean;
}

// Correlation Search and Compliance Reports lead as the two remaining at-a-glance tabs, per user
// direction: every other per-row governance log is fully reachable by Correlation ID, so those tabs'
// menu entries are suppressed (see the `hidden` doc comment above) even though their routes stay live.
const GOVERNANCE_TABS: GovernanceTab[] = [
  { label: 'Audit Logs', route: 'audit-logs', icon: 'history_edu', permissions: ['governance.read'], hidden: true },
  { label: 'Correlation Search', route: 'correlation-search', icon: 'search', permissions: ['governance.read'] },
  { label: 'Authentication Logs', route: 'authentication-logs', icon: 'login', permissions: ['governance.read'], hidden: true },
  { label: 'SMART Launch Logs', route: 'smart-launch-logs', icon: 'launch', permissions: ['governance.read'], hidden: true },
  { label: 'OAuth', route: 'oauth-logs', icon: 'vpn_key', permissions: ['governance.read'], hidden: true },
  { label: 'Authorization Logs', route: 'authorization-logs', icon: 'block', permissions: ['governance.read'], hidden: true },
  { label: 'Data Access Logs', route: 'data-access-logs', icon: 'visibility', permissions: ['governance.read'], hidden: true },
  { label: 'Security Events', route: 'security-events', icon: 'shield', permissions: ['governance.read'], hidden: true },
  { label: 'Compliance Reports', route: 'compliance-reports', icon: 'description', permissions: ['governance.read'] },
  { label: 'Retention Policies', route: 'retention-policies', icon: 'event_repeat', permissions: ['governance.read'], hidden: true },
  { label: 'Archive', route: 'archive', icon: 'archive', permissions: ['governance.read'], hidden: true },
  { label: 'Log Settings', route: 'log-settings', icon: 'settings', permissions: ['governance.read'], hidden: true },
  { label: 'Alerts', route: 'alerts', icon: 'notifications_active', permissions: ['governance.read'], hidden: true },
  { label: 'Alert Rules', route: 'alert-rules', icon: 'rule', permissions: ['governance.read'], hidden: true },
];

@Component({
  selector: 'app-governance-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './governance-shell.component.html',
  styleUrl: './governance-shell.component.scss',
})
export class GovernanceShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as the sidebar and the Settings/Operations shells.
  readonly tabs = computed<GovernanceTab[]>(() =>
    GOVERNANCE_TABS.filter(tab => {
      if (tab.hidden) return false;
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
