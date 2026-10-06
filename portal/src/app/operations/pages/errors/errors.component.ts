import { Component, Input, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ToastService } from '../../../services/toast.service';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorLogEntry, ErrorLogSearch, PagedResult } from '../../models/operations.model';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { ErrorDetailDialogComponent, ErrorListTarget } from '../../components/error-detail-dialog/error-detail-dialog.component';

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
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule, RouterLink, HideWithoutPermissionDirective, ErrorDetailDialogComponent],
  templateUrl: './errors.component.html',
  styleUrl: './errors.component.scss',
})
export class ErrorsComponent implements OnInit, OnDestroy {
  /** True when hosted inside the Errors page, which supplies the page title and the tabs. */
  @Input() embedded = false;

  @ViewChild(ErrorDetailDialogComponent) private detailDialog?: ErrorDetailDialogComponent;
  private querySub?: Subscription;

  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly loading = signal(false);
  readonly result = signal<PagedResult<ErrorLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  // Phase 6A – Monitoring → Errors search criteria.
  readonly errorReferenceId = signal('');
  readonly correlationId = signal('');
  /** '' = the normal error view; or an entry type such as WorkflowDebug / Warning / Information. */
  readonly severity = signal('');
  /** '' = every category; otherwise one of the backend's ErrorCategory names. */
  readonly category = signal('');
  readonly categoryOptions = ['Unknown', 'Business', 'Validation', 'Infrastructure', 'Authentication', 'Authorization', 'Database', 'Network', 'ExternalSystem'];

  /** The criteria the list below is actually showing (what was last searched), as opposed to what is typed above. */
  readonly applied = signal({ errorReferenceId: '', correlationId: '', severity: '', category: '' });

  /** Typed or picked, but "Search" has not been pressed yet. */
  readonly dirty = computed(() => {
    const a = this.applied();
    return a.errorReferenceId !== this.errorReferenceId().trim()
      || a.correlationId !== this.correlationId()
      || a.severity !== this.severity()
      || a.category !== this.category();
  });

  readonly chips = computed(() => {
    const a = this.applied();
    const chips: { key: 'errorReferenceId' | 'correlationId' | 'severity' | 'category'; label: string; value: string }[] = [];
    if (a.errorReferenceId) { chips.push({ key: 'errorReferenceId', label: 'Reference ID', value: a.errorReferenceId }); }
    if (a.correlationId) { chips.push({ key: 'correlationId', label: 'Correlation ID', value: a.correlationId }); }
    if (a.severity) { chips.push({ key: 'severity', label: 'Type', value: a.severity }); }
    if (a.category) { chips.push({ key: 'category', label: 'Category', value: a.category }); }
    return chips;
  });

  /** Applies what was typed / picked: the one place a search starts. */
  search(): void {
    this.pageIndex.set(0);
    this.syncUrl();
    this.load();
  }

  removeChip(key: 'errorReferenceId' | 'correlationId' | 'severity' | 'category'): void {
    if (key === 'errorReferenceId') { this.errorReferenceId.set(''); }
    if (key === 'correlationId') { this.correlationId.set(''); }
    if (key === 'severity') { this.severity.set(''); }
    if (key === 'category') { this.category.set(''); }
    this.pageIndex.set(0);
    this.syncUrl();
    this.load();
  }

  /** Keeps the address bar in step with the applied criteria so the view can be bookmarked or shared. */
  private syncUrl(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        errorReferenceId: this.errorReferenceId().trim() || null,
        correlationId: this.correlationId() || null,
      },
      queryParamsHandling: 'merge',
    });
  }


  readonly displayedCols = [
    'occurredOnUtc', 'errorReferenceId', 'module', 'message', 'whatToDo', 'correlationId', 'actions',
  ];

  ngOnInit(): void {
    // Seed from query params so a deep-link (?errorReferenceId=… or ?correlationId=…) lands pre-filtered, and keep
    // following them: the Overview tab and the error popup send people here by changing these same params.
    // correlationId pulls up every error from a run; errorReferenceId identifies only the one row it was minted for
    // (ErrorLogs.ErrorReferenceId is unique per row, never shared across a run).
    this.querySub = this.route.queryParamMap.subscribe(params => {
      const reference = params.get('errorReferenceId') ?? '';
      const correlation = params.get('correlationId') ?? '';
      const unchanged = this.initialised
        && reference === this.errorReferenceId() && correlation === this.correlationId();
      this.errorReferenceId.set(reference);
      this.correlationId.set(correlation);
      if (!unchanged) {
        this.pageIndex.set(0);
        this.load();
      }

      this.initialised = true;
    });
  }

  ngOnDestroy(): void {
    this.querySub?.unsubscribe();
  }

  private initialised = false;

  /** Opens the shared error popup for a row of this list. */
  openDetail(entry: ErrorLogEntry): void {
    this.detailDialog?.open(entry);
  }

  /** The popup asked to see an error / a whole run in this list. */
  applyTarget(target: ErrorListTarget): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        view: 'list',
        errorReferenceId: target.errorReferenceId ?? null,
        correlationId: target.correlationId ?? null,
      },
      queryParamsHandling: 'merge',
    });
  }

  load(): void {
    this.loading.set(true);
    this.applied.set({ errorReferenceId: this.errorReferenceId().trim(), correlationId: this.correlationId(), severity: this.severity(), category: this.category() });
    const search: ErrorLogSearch = {
      errorReferenceId: this.errorReferenceId() || undefined,
      correlationId: this.correlationId() || undefined,
      severity: this.severity() || undefined,
      category: this.category() || undefined,
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
    this.severity.set('');
    this.category.set('');
    this.pageIndex.set(0);
    // Clear the deep-link params too, otherwise they would be re-applied the next time this tab opens.
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { errorReferenceId: null, correlationId: null },
      queryParamsHandling: 'merge',
    });
    this.load();
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
