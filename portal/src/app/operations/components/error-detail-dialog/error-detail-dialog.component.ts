import { Component, ElementRef, EventEmitter, Output, ViewChild, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorCorrelation, ErrorLogEntry } from '../../models/operations.model';

/** Where "Show in list" should take the user. */
export interface ErrorListTarget {
  errorReferenceId?: string;
  correlationId?: string;
}

/**
 * The one place a single error is shown in full - used by both the Overview and the All errors list, so an error
 * looks and behaves the same wherever it is opened: every recorded field, technical details, the run's correlation
 * timeline and copy-for-support.
 */
@Component({
  selector: 'app-error-detail-dialog',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './error-detail-dialog.component.html',
  styleUrl: './error-detail-dialog.component.scss',
})
export class ErrorDetailDialogComponent {
  private readonly api = inject(OperationsApiService);
  private readonly toast = inject(ToastService);

  /** The user chose to see this error / run in the All errors list. */
  @Output() readonly showInList = new EventEmitter<ErrorListTarget>();

  @ViewChild('dialog') private dialog?: ElementRef<HTMLDialogElement>;

  readonly detail = signal<ErrorLogEntry | null>(null);
  readonly loading = signal(false);
  readonly missing = signal(false);
  readonly correlation = signal<ErrorCorrelation | null>(null);
  readonly correlationLoading = signal(false);
  readonly correlationFailed = signal(false);
  readonly correlationError = signal('');

  /** Opens the popup for a row already in hand, or looks one up by its reference id. */
  open(source: string | ErrorLogEntry | null): void {
    this.detail.set(null);
    this.missing.set(false);
    this.correlation.set(null);
    this.correlationFailed.set(false);
    this.dialog?.nativeElement.showModal();

    if (source && typeof source !== 'string') {
      this.detail.set(source);
      return;
    }

    if (!source) {
      this.missing.set(true);
      return;
    }

    this.fetch(source);
  }

  close(): void {
    this.dialog?.nativeElement.close();
  }

  private fetch(errorReferenceId: string): void {
    this.loading.set(true);
    this.api.searchErrors({ errorReferenceId, page: 1, pageSize: 1 }).subscribe({
      next: result => {
        const entry = result.items[0] ?? null;
        this.detail.set(entry);
        this.missing.set(!entry);
        this.loading.set(false);
      },
      error: () => { this.missing.set(true); this.loading.set(false); },
    });
  }

  /** Loads the run timeline (scheduler, API calls, retries, other errors, exports, workflow trace…) behind this error. */
  loadCorrelation(): void {
    const id = this.detail()?.correlationId;
    if (!id) { return; }
    this.correlationLoading.set(true);
    this.correlationFailed.set(false);
    this.api.errorCorrelation(id).subscribe({
      next: result => { this.correlation.set(result); this.correlationLoading.set(false); },
      error: (err: { status?: number }) => {
        this.correlationError.set(err?.status === 404
          ? 'The server does not have this feature yet (HTTP 404) - restart the API so it picks up the latest version.'
          : `The server returned an error${err?.status ? ' (HTTP ' + err.status + ')' : ''}.`);
        this.correlationFailed.set(true);
        this.correlationLoading.set(false);
      },
    });
  }

  showReferenceInList(): void {
    const e = this.detail();
    this.close();
    this.showInList.emit({ errorReferenceId: e?.errorReferenceId ?? undefined });
  }

  showRunInList(): void {
    const e = this.detail();
    this.close();
    this.showInList.emit({ correlationId: e?.correlationId ?? undefined });
  }

  copyDetail(): void {
    const e = this.detail();
    if (!e) { return; }
    const lines = [
      e.errorReferenceId ? `Reference ID: ${e.errorReferenceId}` : null,
      `Time (UTC): ${e.occurredOnUtc}`,
      `Severity: ${e.severity}`,
      e.category ? `Category: ${e.category}` : null,
      e.module ? `Area: ${e.module}` : null,
      e.workflowName ? `Workflow: ${e.workflowName}` : null,
      e.nodeName ? `Node: ${e.nodeName}${e.nodeType ? ' (' + e.nodeType + ')' : ''}` : null,
      e.sourceName ? `Source: ${e.sourceName}` : null,
      e.destinationName ? `Destination: ${e.destinationName}` : null,
      e.resourceType ? `Resource type: ${e.resourceType}` : null,
      `Error: ${e.exceptionType}`,
      `Message: ${e.message}`,
      e.diagnosisCause ? `Cause: ${e.diagnosisCause}` : null,
      e.correlationId ? `Correlation ID: ${e.correlationId}` : null,
      e.executionId ? `Execution ID: ${e.executionId}` : null,
      e.workflowId ? `Workflow ID: ${e.workflowId}` : null,
      e.endpointId ? `Endpoint: ${e.endpointId}` : null,
      e.traceId ? `Trace ID: ${e.traceId}` : null,
      e.stackTrace ? `\nTechnical details:\n${e.stackTrace}` : null,
    ].filter((l): l is string => l !== null);
    navigator.clipboard.writeText(lines.join('\n')).then(
      () => this.toast.success('Error details copied.'),
      () => this.toast.error('Could not copy to the clipboard.'),
    );
  }
}
