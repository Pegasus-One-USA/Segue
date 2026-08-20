import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { catchError, of } from 'rxjs';
import { ExecutionHistoryApiService } from '../../execution-history/services/execution-history-api.service';
import { ToastService } from '../../services/toast.service';
import { PipelineRun, PipelineRunStatus, mapRouteExecution } from '../models/pipeline-run.model';

/** How many recent runs the dashboard's Recent Pipelines table shows. */
const RECENT_COUNT = 10;

/** Backs the Dashboard's "Recent Pipelines" table with the same workflow-run data (and the same
 *  StartedAt-descending ordering, via sortColumn: 'lastRun') that Execution History already pulls —
 *  see execution-history-list.component.ts's default sort. No longer mock data. */
@Injectable({ providedIn: 'root' })
export class PipelineRunService {
  private readonly api   = inject(ExecutionHistoryApiService);
  private readonly toast = inject(ToastService);

  readonly runs    = signal<PipelineRun[]>([]);
  readonly loading = signal(false);

  readonly executionCounts = computed(() => {
    const zero: Record<PipelineRunStatus, number> = {
      running: 0, completed: 0, completedWithErrors: 0, failed: 0, skipped: 0, queued: 0, cancelled: 0,
    };
    return this.runs().reduce((acc, r) => ({ ...acc, [r.status]: acc[r.status] + 1 }), zero);
  });

  constructor() {
    this.fetchRecent();
  }

  fetchRecent(): void {
    this.loading.set(true);

    this.api
      .list({
        page: 1,
        pageSize: RECENT_COUNT,
        sortColumn: 'lastRun',
        sortDirection: 'desc',
      })
      .pipe(
        catchError((err: HttpErrorResponse) => {
          // Dashboard is reachable by every authenticated role regardless of workflow.view (it's a
          // mandatory platform entry point, not an RBAC-controlled permission) — but this table's
          // data still comes from the same workflow.view-gated endpoint Workflows/Execution History
          // use. A role without it gets a 403 here on every load/auto-refresh; that's expected, not
          // a server error, so it degrades to an empty table silently instead of alarming the user
          // with a misleading "server error" toast (see the stat-tiles' matching handling in
          // dashboard.component.ts's loadRunStatusCounts).
          if (err.status !== 403) {
            this.toast.error('Could not load pipelines', 'A server error occurred. Try again later.');
          }
          return of({ items: [], totalCount: 0, page: 1, pageSize: RECENT_COUNT });
        }),
      )
      .subscribe(result => {
        this.runs.set(result.items.map(mapRouteExecution));
        this.loading.set(false);
      });
  }
}
