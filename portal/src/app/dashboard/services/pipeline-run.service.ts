import { Injectable, computed, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { ExecutionHistoryApiService } from '../../execution-history/services/execution-history-api.service';
import { ToastService } from '../../services/toast.service';
import { PipelineRun, PipelineRunStatus, mapRouteExecution } from '../models/pipeline-run.model';

/** How many recent runs the dashboard's Recent Pipelines table shows. */
const RECENT_COUNT = 10;

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

    // The dashboard shows Runtime Plane workflow runs — the same source as the Execution History screen
    // (/api/v1/workflow-runs), which is where "Run" in the Workflow Builder records its executions.
    this.api
      .list({ page: 1, pageSize: RECENT_COUNT })
      .pipe(
        catchError(() => {
          this.toast.error('Could not load pipelines', 'A server error occurred. Try again later.');
          return of({ items: [], totalCount: 0, page: 1, pageSize: RECENT_COUNT });
        }),
      )
      .subscribe(result => {
        this.runs.set(result.items.map(mapRouteExecution));
        this.loading.set(false);
      });
  }
}
