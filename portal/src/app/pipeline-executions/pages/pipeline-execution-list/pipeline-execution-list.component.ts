import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { PipelineExecutionApiService } from '../../services/pipeline-execution-api.service';
import { PipelineExecutionEntry } from '../../models/pipeline-execution.model';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-pipeline-execution-list',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule, RouterLink],
  templateUrl: './pipeline-execution-list.component.html',
  styleUrl: './pipeline-execution-list.component.scss',
})
export class PipelineExecutionListComponent implements OnInit {
  private readonly api = inject(PipelineExecutionApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly loading = signal(false);
  readonly entries = signal<PipelineExecutionEntry[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly perPage = signal(10);

  readonly search = signal('');
  readonly status = signal('');

  readonly cancellingRunIds = signal<Set<string>>(new Set());

  readonly displayedCols = ['pipelineName', 'sourceName', 'startedOnUtc', 'durationMs', 'status', 'triggeredBy', 'counts', 'correlationId', 'actions'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.list(this.page(), this.perPage(), this.status() || undefined, undefined, undefined, this.search() || undefined).subscribe({
      next: result => {
        this.entries.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onSearchChange(value: string): void {
    this.search.set(value);
  }

  onStatusChange(value: string): void {
    this.status.set(value);
  }

  applyFilters(): void {
    this.page.set(1);
    this.load();
  }

  reset(): void {
    this.search.set('');
    this.status.set('');
    this.page.set(1);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.page.set(e.pageIndex + 1);
    this.perPage.set(e.pageSize);
    this.load();
  }

  openDetail(entry: PipelineExecutionEntry): void {
    this.router.navigate(['/operations/pipeline-executions', entry.id]);
  }

  viewCorrelation(entry: PipelineExecutionEntry, event: Event): void {
    event.stopPropagation();
    if (entry.correlationId) {
      this.router.navigate(['/governance/correlation-search'], { queryParams: { correlationId: entry.correlationId } });
    }
  }

  /** Cancels the WHOLE pipeline run batch this route execution belongs to (see pipelineRunId), not just this
   *  one row — a Configured Pipeline run can process several routes/resource types in one pass, and there is
   *  no per-route cancellation, only per-batch. Steps already completed are not undone; a re-run can duplicate
   *  data at destinations not configured for Upsert, so the confirmation says so plainly. */
  cancelRun(entry: PipelineExecutionEntry, event: Event): void {
    event.stopPropagation();

    this.dialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '480px',
        data: {
          title: 'Cancel this pipeline run?',
          message: 'This stops the whole run (every route it processes) after its current step finishes. Steps already completed are not undone — re-running later may duplicate data at destinations not configured for Upsert (plain SQL insert, file/blob writers).',
          confirmLabel: 'Cancel Run',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;

        this.cancellingRunIds.update(ids => new Set(ids).add(entry.pipelineRunId));
        this.api.cancel(entry.pipelineRunId).subscribe({
          next: () => {
            this.toast.show('Cancellation requested', 'The run will stop once its current step finishes.');
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            this.cancellingRunIds.update(ids => {
              const next = new Set(ids);
              next.delete(entry.pipelineRunId);
              return next;
            });
            const message = err.status === 409
              ? 'This run has already finished and cannot be cancelled.'
              : 'Failed to request cancellation. Please try again.';
            this.toast.error(message);
          },
        });
      });
  }
}
