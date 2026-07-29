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
   *  Data Access Logs. Route and component are kept intact for all of these; only the menu entry is
   *  suppressed. */
  hidden?: boolean;
}

// Audit Logs leads as the at-a-glance overview tab, per user direction consolidating the former
// 14-item sidebar section into this single tabbed menu. Configuration Comparison and Data Lineage
// are reachable-by-drill-down-only pages (never had their own sidebar entry) so they stay nested
// under this shell's routes without appearing in the tab strip itself.
const GOVERNANCE_TABS: GovernanceTab[] = [
  { label: 'Audit Logs', route: 'audit-logs', icon: 'history_edu', permissions: ['governance.read'] },
  { label: 'Correlation Search', route: 'correlation-search', icon: 'search', permissions: ['governance.read'] },
  { label: 'Authentication Logs', route: 'authentication-logs', icon: 'login', permissions: ['governance.read'] },
  { label: 'SMART Launch Logs', route: 'smart-launch-logs', icon: 'launch', permissions: ['governance.read'] },
  { label: 'OAuth', route: 'oauth-logs', icon: 'vpn_key', permissions: ['governance.read'], hidden: true },
  { label: 'Authorization Logs', route: 'authorization-logs', icon: 'block', permissions: ['governance.read'] },
  { label: 'Data Access Logs', route: 'data-access-logs', icon: 'visibility', permissions: ['governance.read'], hidden: true },
  { label: 'Security Events', route: 'security-events', icon: 'shield', permissions: ['governance.read'] },
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
