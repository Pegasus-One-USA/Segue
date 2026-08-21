import { Component, DestroyRef, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime } from 'rxjs/operators';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { PipelineTableComponent } from '../../components/pipeline-table/pipeline-table.component';
import { ExecutionHistoryApiService } from '../../../execution-history/services/execution-history-api.service';
import { WorkflowRunStatusCounts } from '../../../execution-history/models/execution-history.model';
import { RunStatusHubService } from '../../../services/run-status-hub.service';
import { PermissionService } from '../../../auth/services/permission.service';

const EMPTY_RUN_STATUS_COUNTS: WorkflowRunStatusCounts = {
  pending: 0,
  running: 0,
  succeeded: 0,
  failed: 0,
  cancelled: 0,
};

// SignalR (RunStatusHubService) is the primary "keep this live" mechanism — see the constructor. This interval
// is the fallback for whenever the hub can't connect at all (proxy blocking WebSockets, or it gave up
// reconnecting): a run that started/finished while the Dashboard was open would otherwise sit there stale until
// the admin manually hit Refresh.
const AUTO_REFRESH_MS = 15_000;
// A burst of RunStatusChanged events (several runs finishing within the same second) collapses into one refetch
// instead of one per event.
const EVENT_REFRESH_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    MatIconModule,
    PipelineTableComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly dashSvc = inject(DashboardService);
  private readonly runSvc  = inject(PipelineRunService);
  private readonly executionHistoryApi = inject(ExecutionHistoryApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly runStatusHub = inject(RunStatusHubService);
  private readonly permissions  = inject(PermissionService);

  // Mirrors WorkflowListComponent's canCreate() — its identical "+ New Workflow" button is already
  // gated on workflow.create; this entry point must not offer a dead end to a role that can't
  // actually create one.
  canCreateWorkflow(): boolean {
    return this.permissions.hasPermission('workflow.create');
  }

  protected readonly runs          = this.runSvc.runs;
  protected readonly lastRefreshed = this.dashSvc.lastRefreshed;

  // All-time workflow-run status counts (Dashboard stat tiles) — a separate, all-time aggregate from
  // `runs()` above, which is only the most recent page of pipeline runs.
  protected readonly runStatusCounts = signal<WorkflowRunStatusCounts>(EMPTY_RUN_STATUS_COUNTS);

  constructor() {
    this.loadRunStatusCounts();

    // Fallback only — see AUTO_REFRESH_MS. SignalR below is what actually keeps this live; this interval just
    // guarantees the screen still catches up eventually if the hub never connects at all.
    const handle = setInterval(() => this.refresh(), AUTO_REFRESH_MS);
    this.destroyRef.onDestroy(() => clearInterval(handle));

    // A RunStatusChangedEvent carries only ids/status, not a full RouteExecution row (pipeline name, source,
    // etc.) — a targeted refetch of the recent-runs page + stat counts is the correct, always-consistent
    // response to "something changed", not an attempt to hand-patch a row from a payload that can't fully
    // describe it. Still push-driven (near-instant) rather than waiting for the next 15s tick.
    this.runStatusHub.ensureConnected();
    this.runStatusHub.runStatusChanged$
      .pipe(debounceTime(EVENT_REFRESH_DEBOUNCE_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh());

    // Reconciles anything missed while disconnected (initial connect included) — same reasoning as the debounced
    // event handler above, just triggered by connection state instead of a specific event.
    this.runStatusHub.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh());
  }

  private loadRunStatusCounts(): void {
    this.executionHistoryApi.statusCounts().subscribe({
      next: counts => this.runStatusCounts.set(counts),
      error: () => this.runStatusCounts.set(EMPTY_RUN_STATUS_COUNTS),
    });
  }

  refresh(): void {
    this.dashSvc.refresh();
    this.runSvc.fetchRecent();
    this.loadRunStatusCounts();
  }

  onActionClick(_id: string): void {}
}
