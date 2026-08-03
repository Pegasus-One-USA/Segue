import { Injectable, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { ExecutionHistoryApiService } from '../../execution-history/services/execution-history-api.service';
import { RouteExecution } from '../../execution-history/models/execution-history.model';
import { ToastService } from '../../services/toast.service';

/** How many recent runs the dashboard's Recent Pipelines table shows. */
const RECENT_COUNT = 10;

/** Backs the Dashboard's "Recent Pipelines" table with the same workflow-run data (and the same
 *  StartedAt-descending ordering, via sortColumn: 'lastRun') that Execution History already pulls —
 *  see execution-history-list.component.ts's default sort. No longer mock data. */
@Injectable({ providedIn: 'root' })
export class PipelineRunService {
  private readonly api   = inject(ExecutionHistoryApiService);
  private readonly toast = inject(ToastService);

  readonly runs    = signal<RouteExecution[]>([]);
  readonly loading = signal(false);

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
        this.runs.set(result.items);
        this.loading.set(false);
      });
  }
}
