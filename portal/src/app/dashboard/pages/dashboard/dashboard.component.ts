import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { PipelineTableComponent } from '../../components/pipeline-table/pipeline-table.component';
import { ExecutionHistoryApiService } from '../../../execution-history/services/execution-history-api.service';
import { WorkflowRunStatusCounts } from '../../../execution-history/models/execution-history.model';

const EMPTY_RUN_STATUS_COUNTS: WorkflowRunStatusCounts = {
  pending: 0,
  running: 0,
  succeeded: 0,
  failed: 0,
  cancelled: 0,
};

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

  protected readonly runs          = this.runSvc.runs;
  protected readonly lastRefreshed = this.dashSvc.lastRefreshed;

  // All-time workflow-run status counts (Dashboard stat tiles) — a separate, all-time aggregate from
  // `runs()` above, which is only the most recent page of pipeline runs.
  protected readonly runStatusCounts = signal<WorkflowRunStatusCounts>(EMPTY_RUN_STATUS_COUNTS);

  constructor() {
    this.loadRunStatusCounts();
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
