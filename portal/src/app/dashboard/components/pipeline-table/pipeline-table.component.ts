import { Component, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { PipelineRun, PipelineRunStatus } from '../../models/pipeline-run.model';

const STATUS_LABELS: Record<PipelineRunStatus, string> = {
  running:   'Running',
  completed: 'Completed',
  failed:    'Failed',
  queued:    'Queued',
  cancelled: 'Cancelled',
};

@Component({
  selector: 'app-pipeline-table',
  standalone: true,
  imports: [DatePipe, RouterLink],
  templateUrl: './pipeline-table.component.html',
  styleUrl: './pipeline-table.component.scss',
})
export class PipelineTableComponent {
  readonly runs        = input<PipelineRun[]>([]);
  readonly runClicked  = output<string>();
  readonly editClicked = output<string>();
  readonly logsClicked = output<string>();

  statusLabel(s: PipelineRunStatus): string {
    return STATUS_LABELS[s];
  }

  formatDuration(ms?: number): string {
    if (!ms) return '—';
    if (ms < 1000)  return `${ms}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`;
  }
}
