import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorLogEntry, ErrorLogSearch } from '../../models/operations.model';

@Component({
  selector: 'app-errors',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './errors.component.html',
  styleUrl: './errors.component.scss',
})
export class ErrorsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly entries = signal<ErrorLogEntry[]>([]);
  readonly expandedId = signal<string | null>(null);

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
    'occurredOnUtc', 'errorReferenceId', 'severity', 'category', 'status', 'module', 'exceptionType', 'message', 'correlationId', 'actions',
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
    };
    this.api.searchErrors(search).subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  reset(): void {
    this.errorReferenceId.set('');
    this.correlationId.set('');
    this.executionId.set('');
    this.workflowId.set('');
    this.severity.set('');
    this.category.set('');
    this.status.set('');
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
}
