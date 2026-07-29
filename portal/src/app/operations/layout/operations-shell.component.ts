import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';

interface OperationsTab {
  label: string;
  route: string;
  icon: string;
  permissions?: string[];
  /** Hidden from the tab menu. System Health/Queue Monitor/API Analytics mislead under multi-replica/
   *  container deployment (per-container-only data, resets on restart/scale) — see
   *  docs/OPERATIONS_TABS_ANALYSIS.md §2. Pipeline Executions/Scheduler History/Retry History are hidden
   *  per user direction: Pipeline Executions has no authoring UI anywhere in the portal (nothing creates
   *  the ResourcePipelineRoute rows it displays), and Scheduler History/Retry History are narrow, mostly-
   *  empty diagnostics with no failure state of their own. Endpoint Health is hidden per user direction
   *  too. API Requests, Exports, Notifications, and Validation Failures are hidden per user direction as
   *  well, since every one of them is fully searchable by Correlation ID via the Correlation Search tab
   *  (in the Governance shell). Routes and components are kept intact; only the menu entry is
   *  suppressed. */
  hidden?: boolean;
}

// Errors leads as the sole remaining tab, per user direction: every other per-row operations log is
// fully reachable by Correlation ID, so those tabs' menu entries are suppressed (see the `hidden` doc
// comment above) even though their routes stay live.
const OPERATIONS_TABS: OperationsTab[] = [
  { label: 'System Health', route: 'system-health', icon: 'monitor_heart', permissions: ['governance.read'], hidden: true },
  { label: 'Pipeline Executions', route: 'pipeline-executions', icon: 'bolt', permissions: ['governance.read'], hidden: true },
  { label: 'Queue Monitor', route: 'queue-monitor', icon: 'inbox', permissions: ['governance.read'], hidden: true },
  { label: 'API Analytics', route: 'api-analytics', icon: 'analytics', permissions: ['governance.read'], hidden: true },
  { label: 'Scheduler History', route: 'scheduler-history', icon: 'schedule', permissions: ['governance.read'], hidden: true },
  { label: 'Retry History', route: 'retry-history', icon: 'replay', permissions: ['governance.read'], hidden: true },
  { label: 'Errors', route: 'errors', icon: 'error_outline', permissions: ['governance.read'] },
  { label: 'API Requests', route: 'api-requests', icon: 'swap_horiz', permissions: ['governance.read'], hidden: true },
  { label: 'Exports', route: 'exports', icon: 'file_download', permissions: ['governance.read'], hidden: true },
  { label: 'Notifications', route: 'notifications', icon: 'notifications', permissions: ['governance.read'], hidden: true },
  { label: 'Validation Failures', route: 'validation-failures', icon: 'fact_check', permissions: ['governance.read'], hidden: true },
  { label: 'Endpoint Health', route: 'endpoint-health', icon: 'favorite', permissions: ['governance.read'], hidden: true },
];

@Component({
  selector: 'app-operations-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './operations-shell.component.html',
  styleUrl: './operations-shell.component.scss',
})
export class OperationsShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as the sidebar and the Settings shell.
  readonly tabs = computed<OperationsTab[]>(() =>
    OPERATIONS_TABS.filter(tab => {
      if (tab.hidden) return false;
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
