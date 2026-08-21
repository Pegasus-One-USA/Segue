import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';
import { LOGS_COMPLIANCE_TABS } from '../../core/logs-compliance-nav';

// This shell also hosts routes beyond the ones surfaced in the shared "Logs & Compliance" tab strip
// (LOGS_COMPLIANCE_TABS, imported below) — errors resolves as this shell's own child per
// app.routes.ts, while system-health, pipeline-executions, queue-monitor, api-analytics,
// scheduler-history, retry-history, api-requests, exports, notifications, validation-failures, and
// endpoint-health stay reachable by URL with no menu entry. System Health/Queue Monitor/API Analytics
// mislead under multi-replica/container deployment (per-container-only data, resets on restart/scale)
// — see docs/OPERATIONS_TABS_ANALYSIS.md §2. Pipeline Executions/Scheduler History/Retry History are
// hidden per user direction: Pipeline Executions has no authoring UI anywhere in the portal (nothing
// creates the ResourcePipelineRoute rows it displays), and Scheduler History/Retry History are narrow,
// mostly-empty diagnostics with no failure state of their own. Endpoint Health is hidden per user
// direction too. API Requests, Exports, Notifications, and Validation Failures are hidden per user
// direction as well, since every one of them is fully searchable by Correlation ID via the Correlation
// Search tab.

@Component({
  selector: 'app-operations-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './operations-shell.component.html',
  styleUrl: './operations-shell.component.scss',
})
export class OperationsShellComponent {
  private readonly store = inject(AuthStore);

  // The shared "Logs & Compliance" tab strip (see logs-compliance-nav.ts) — identical whether this
  // shell or governance-shell.component.ts is currently mounted, so the header menu never appears to
  // shrink just because a tab (e.g. Errors) happens to route into this shell.
  readonly tabs = computed(() =>
    LOGS_COMPLIANCE_TABS.filter(tab => {
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
