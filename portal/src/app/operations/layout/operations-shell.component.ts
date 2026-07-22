import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../../auth/store/auth.store';

interface OperationsTab {
  label: string;
  route: string;
  permissions?: string[];
}

// System Health leads as the at-a-glance overview tab, per user direction consolidating the
// former 12-item sidebar section into this single tabbed menu.
const OPERATIONS_TABS: OperationsTab[] = [
  { label: 'System Health', route: 'system-health', permissions: ['governance.read'] },
  { label: 'Pipeline Executions', route: 'pipeline-executions', permissions: ['governance.read'] },
  { label: 'Queue Monitor', route: 'queue-monitor', permissions: ['governance.read'] },
  { label: 'API Analytics', route: 'api-analytics', permissions: ['governance.read'] },
  { label: 'Scheduler History', route: 'scheduler-history', permissions: ['governance.read'] },
  { label: 'Retry History', route: 'retry-history', permissions: ['governance.read'] },
  { label: 'Errors', route: 'errors', permissions: ['governance.read'] },
  { label: 'API Requests', route: 'api-requests', permissions: ['governance.read'] },
  { label: 'Exports', route: 'exports', permissions: ['governance.read'] },
  { label: 'Notifications', route: 'notifications', permissions: ['governance.read'] },
  { label: 'Validation Failures', route: 'validation-failures', permissions: ['governance.read'] },
  { label: 'Endpoint Health', route: 'endpoint-health', permissions: ['governance.read'] },
];

@Component({
  selector: 'app-operations-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './operations-shell.component.html',
  styleUrl: './operations-shell.component.scss',
})
export class OperationsShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as the sidebar and the Settings shell.
  readonly tabs = computed<OperationsTab[]>(() =>
    OPERATIONS_TABS.filter(tab => {
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
