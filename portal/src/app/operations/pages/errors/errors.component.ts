import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ToastService } from '../../../services/toast.service';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorLogEntry, ErrorLogSearch, PagedResult } from '../../models/operations.model';

/** Fallback only, for rows captured before the backend started computing `diagnosisAction`
 *  (docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §8) — categories a customer can typically resolve themselves.
 *  Once every row carries a real diagnosisAction this fallback stops mattering; kept only so historical rows
 *  still render something reasonable instead of blank. */
const SELF_FIXABLE_CATEGORIES = new Set([
  'Network', 'Database', 'ExternalSystem', 'Authentication', 'Authorization', 'Validation',
]);

@Component({
  selector: 'app-errors',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule, RouterLink],
  templateUrl: './errors.component.html',
  styleUrl: './errors.component.scss',
})
export class ErrorsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  readonly loading = signal(false);
  readonly result = signal<PagedResult<ErrorLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly expandedId = signal<string | null>(null);
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  // Phase 6A – Monitoring → Errors search criteria.
  readonly errorReferenceId = signal('');
  readonly correlationId = signal('');
  readonly executionId = signal('');
  readonly workflowId = signal('');
  readonly severity = signal('');
  readonly category = signal('');
  readonly status = signal('');

  readonly categories = ['Business', 'Validation', 'Infrastructure', 'Authentication', 'Authorization', 'Database', 'Network', 'ExternalSystem', 'Unknown'];
  readonly severities = ['Error', 'Warning'];
  readonly statuses = ['Open', 'Resolved'];

  readonly displayedCols = [
    'occurredOnUtc', 'errorReferenceId', 'module', 'message', 'whatToDo', 'status', 'correlationId', 'actions',
  ];

  ngOnInit(): void {
    // Seed from query params so a support engineer's deep-link (?errorReferenceId=… or ?correlationId=…) lands pre-filtered.
    const params = this.route.snapshot.queryParamMap;
    this.errorReferenceId.set(params.get('errorReferenceId') ?? '');
    this.correlationId.set(params.get('correlationId') ?? '');
    this.executionId.set(params.get('executionId') ?? '');
    this.load();
  }

  load(): void {
    this.loading.set(true);
    const search: ErrorLogSearch = {
      errorReferenceId: this.errorReferenceId() || undefined,
      correlationId: this.correlationId() || undefined,
      executionId: this.executionId() || undefined,
      workflowId: this.workflowId() || undefined,
      severity: this.severity() || undefined,
      category: this.category() || undefined,
      status: this.status() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    };
    this.api.searchErrors(search).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  reset(): void {
    this.errorReferenceId.set('');
    this.correlationId.set('');
    this.executionId.set('');
    this.workflowId.set('');
    this.severity.set('');
    this.category.set('');
    this.status.set('');
    this.pageIndex.set(0);
    this.load();
  }

  toggleStackTrace(id: string): void {
    this.expandedId.set(this.expandedId() === id ? null : id);
  }

  resolve(entry: ErrorLogEntry): void {
    if (!entry.errorReferenceId) { return; }
    this.api.resolveError(entry.errorReferenceId).subscribe({ next: () => this.load() });
  }

  reopen(entry: ErrorLogEntry): void {
    if (!entry.errorReferenceId) { return; }
    this.api.reopenError(entry.errorReferenceId).subscribe({ next: () => this.load() });
  }

  severityClass(severity: string): string {
    return 'severity-' + severity.toLowerCase();
  }

  /** Backend-computed diagnosis wins when present; category-shape guessing is only a fallback for rows that
   *  predate the diagnosisAction field. */
  isSelfFixable(entry: Pick<ErrorLogEntry, 'diagnosisAction' | 'category'>): boolean {
    if (entry.diagnosisAction) {
      return entry.diagnosisAction === 'SelfFix';
    }
    return !!entry.category && SELF_FIXABLE_CATEGORIES.has(entry.category);
  }

  actionLabel(entry: Pick<ErrorLogEntry, 'diagnosisAction' | 'category'>): string {
    return this.isSelfFixable(entry) ? 'Check your configuration' : 'Contact support';
  }

  copyForSupport(entry: ErrorLogEntry): void {
    const lines = [
      entry.errorReferenceId ? `Reference ID: ${entry.errorReferenceId}` : null,
      entry.correlationId ? `Correlation ID: ${entry.correlationId}` : null,
      `Occurred: ${entry.occurredOnUtc}`,
    ].filter((line): line is string => !!line);

    navigator.clipboard.writeText(lines.join('\n')).then(
      () => this.toast.show('Copied', 'Reference details copied — paste them into your support ticket.'),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }
}
