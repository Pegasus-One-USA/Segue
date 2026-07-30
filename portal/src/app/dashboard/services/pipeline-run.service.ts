import { Injectable, inject, signal } from '@angular/core';
import { ExecutionHistoryApiService } from '../../execution-history/services/execution-history-api.service';
import { RouteExecution } from '../../execution-history/models/execution-history.model';

const RECENT_RUNS_COUNT = 10;

/** Backs the Dashboard's "Recent Pipelines" table with the same workflow-run data (and the same
 *  StartedAt-descending ordering, via sortColumn: 'lastRun') that Execution History already pulls —
 *  see execution-history-list.component.ts's default sort. No longer mock data. */
@Injectable({ providedIn: 'root' })
export class PipelineRunService {
  private readonly api = inject(ExecutionHistoryApiService);

  readonly runs = signal<RouteExecution[]>([]);

  constructor() {
    this.fetchRecent();
  }

  fetchRecent(): void {
    this.api.list({
      page: 1,
      pageSize: RECENT_RUNS_COUNT,
      sortColumn: 'lastRun',
      sortDirection: 'desc',
    }).subscribe({
      next: result => this.runs.set(result.items),
      error: () => this.runs.set([]),
    });
  }
}
