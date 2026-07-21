import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../../auth/store/auth.store';

interface GovernanceTab {
  label: string;
  route: string;
  permissions?: string[];
}

// Audit Logs leads as the at-a-glance overview tab, per user direction consolidating the former
// 14-item sidebar section into this single tabbed menu. Configuration Comparison and Data Lineage
// are reachable-by-drill-down-only pages (never had their own sidebar entry) so they stay nested
// under this shell's routes without appearing in the tab strip itself.
const GOVERNANCE_TABS: GovernanceTab[] = [
  { label: 'Audit Logs', route: 'audit-logs', permissions: ['governance.read'] },
  { label: 'Correlation Search', route: 'correlation-search', permissions: ['governance.read'] },
  { label: 'Authentication Logs', route: 'authentication-logs', permissions: ['governance.read'] },
  { label: 'SMART Launch Logs', route: 'smart-launch-logs', permissions: ['governance.read'] },
  { label: 'OAuth', route: 'oauth-logs', permissions: ['governance.read'] },
  { label: 'Authorization Logs', route: 'authorization-logs', permissions: ['governance.read'] },
  { label: 'Data Access Logs', route: 'data-access-logs', permissions: ['governance.read'] },
  { label: 'Security Events', route: 'security-events', permissions: ['governance.read'] },
  { label: 'Compliance Reports', route: 'compliance-reports', permissions: ['governance.read'] },
  { label: 'Retention Policies', route: 'retention-policies', permissions: ['governance.read'] },
  { label: 'Archive', route: 'archive', permissions: ['governance.read'] },
  { label: 'Log Settings', route: 'log-settings', permissions: ['governance.read'] },
  { label: 'Alerts', route: 'alerts', permissions: ['governance.read'] },
  { label: 'Alert Rules', route: 'alert-rules', permissions: ['governance.read'] },
];

@Component({
  selector: 'app-governance-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './governance-shell.component.html',
  styleUrl: './governance-shell.component.scss',
})
export class GovernanceShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as the sidebar and the Settings/Operations shells.
  readonly tabs = computed<GovernanceTab[]>(() =>
    GOVERNANCE_TABS.filter(tab => {
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
