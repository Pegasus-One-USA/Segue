import { Component, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ExecutionStatus, RouteExecution } from '../../../execution-history/models/execution-history.model';

// Reuses the existing .status-badge CSS classes (status-running/completed/failed/queued/cancelled) —
// mapped from the real ExecutionStatus vocabulary rather than renaming the CSS.
const STATUS_LABELS: Record<ExecutionStatus, string> = {
  Pending:            'Pending',
  Running:            'Running',
  Succeeded:          'Completed',
  Failed:             'Failed',
  Cancelled:          'Cancelled',
  PartialSuccess:     'Partial Success',
  AwaitingBulkExport: 'Awaiting Bulk Export',
};

const STATUS_CLASSES: Record<ExecutionStatus, string> = {
  Pending:            'queued',
  Running:            'running',
  Succeeded:          'completed',
  Failed:             'failed',
  Cancelled:          'cancelled',
  PartialSuccess:     'partial',
  AwaitingBulkExport: 'awaiting',
};

@Component({
  selector: 'app-pipeline-table',
  standalone: true,
  imports: [DatePipe, RouterLink],
  templateUrl: './pipeline-table.component.html',
  styleUrl: './pipeline-table.component.scss',
})
export class PipelineTableComponent {
  readonly runs        = input<RouteExecution[]>([]);
  readonly runClicked  = output<string>();
  readonly editClicked = output<string>();
  readonly logsClicked = output<string>();

  sourceLabel(run: RouteExecution): string {
    return run.sourceName ?? run.sourceSystemType ?? '—';
  }

  statusLabel(status: ExecutionStatus): string {
    return STATUS_LABELS[status];
  }

  statusClass(status: ExecutionStatus): string {
    return STATUS_CLASSES[status];
  }
}
