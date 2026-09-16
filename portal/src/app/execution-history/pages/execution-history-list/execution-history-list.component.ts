import { Component, HostListener, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { PagedResult, RouteExecution } from '../../models/execution-history.model';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { sourceSystemDisplayName } from '../../../data/source-system-display-names.data';

type SortColumn = 'pipeline' | 'source' | 'status' | 'duration' | 'lastRun' | 'triggeredBy';
type SortDirection = 'asc' | 'desc';

/** The Source filter's option list is no longer a hardcoded roster of every EHR the platform supports — it comes
 *  from the runs themselves (WorkflowRunHistoryPageDto.availableSourceSystemTypes), so a deployment that only ever
 *  ran Epic and eCW sees two options rather than nine. Same facet contract, multi-select UI and brand labels the
 *  Workflows list uses, so the two screens' Source filters agree. */

@Component({
  selector: 'app-execution-history-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    RouterLink,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    PaginationBarComponent,
  ],
  templateUrl: './execution-history-list.component.html',
  styleUrls: ['./execution-history-list.component.scss'],
})
export class ExecutionHistoryListComponent implements OnInit, OnDestroy {
  private readonly api = inject(ExecutionHistoryApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly search$ = new Subject<string>();

  /** Source systems that actually appear in the run history, from the server's last response. */
  readonly availableSources = signal<string[]>([]);
  readonly selectedSources = signal<Set<string>>(new Set());
  /** True while the Source checkbox panel is open — see toggleSourceMenu / closeSourceMenuIfOutside. */
  readonly sourceMenuOpen = signal(false);

  /** Set when the Workflows list's "Execution History" row action deep-links here (?workflowId=...) — narrows
   *  every load() to that one workflow's runs. Deliberately NOT cleared by reset() (the "Reset filters" button):
   *  it represents which page you're on (a drill-down into one workflow), not a togglable filter alongside
   *  status/source/search — only its own "Show all workflows" control (see clearWorkflowFilter) leaves it. */
  readonly workflowIdFilter = signal<string | null>(null);
  readonly searchQuery   = signal('');
  readonly statusFilter  = signal('');
  readonly triggerFilter = signal('');
  readonly pageIndex     = signal(0);
  readonly pageSize      = signal(10);
  readonly sortColumn    = signal<SortColumn>('lastRun');
  readonly sortDirection = signal<SortDirection>('desc');
  readonly loading       = signal(false);
  /** True only while a debounced search-box request is in flight — drives the small in-field spinner
   *  instead of the app-wide global loader, which dims the screen and inerts the input being typed. */
  readonly searching     = signal(false);
  readonly result        = signal<PagedResult<RouteExecution>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });
  /** Any filter active — drives the two "no results" messages below the table. */
  readonly hasActiveFilters = computed(
    () => !!this.searchQuery() || !!this.statusFilter() || !!this.triggerFilter() || this.selectedSources().size > 0
      || !!this.workflowIdFilter(),
  );

  /** The filtered-to workflow's own name, once a page of its (already server-filtered) runs has loaded — every
   *  row shares the same pipelineName, so there's no need to pass a separate ?workflowName= just for display.
   *  Falls back to a generic label while loading or if the workflow has no runs at all yet. */
  readonly workflowFilterLabel = computed(() =>
    this.workflowIdFilter() ? (this.result().items[0]?.pipelineName ?? 'this workflow') : null,
  );

  readonly displayedCols = ['name', 'source', 'status', 'duration', 'lastRun', 'triggeredBy', 'correlationId'];

  ngOnInit(): void {
    this.search$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.searchQuery.set(value);
      this.pageIndex.set(0);
      this.load(true);
    });

    // A Dashboard stat tile links here with ?status=X (e.g. clicking "Failed") — pre-select that
    // status filter before the first load so the tile's click-through actually lands pre-filtered.
    const statusFromQuery = this.route.snapshot.queryParamMap.get('status');
    if (statusFromQuery) {
      this.statusFilter.set(statusFromQuery);
    }

    // The Workflows list's "Execution History" row action deep-links here with ?workflowId=<id> — narrow to
    // that one workflow's runs before the first load (see workflowIdFilter's own doc comment).
    const workflowIdFromQuery = this.route.snapshot.queryParamMap.get('workflowId');
    if (workflowIdFromQuery) {
      this.workflowIdFilter.set(workflowIdFromQuery);
    }

    this.load();
  }

  ngOnDestroy(): void {
    this.search$.complete();
  }

  /** `silent` comes only from the debounced search box — see ExecutionHistoryApiService.list. */
  load(silent = false): void {
    this.loading.set(true);
    if (silent) this.searching.set(true);
    this.api.list({
      workflowId: this.workflowIdFilter() || undefined,
      status: this.statusFilter() || undefined,
      sources: this.selectedSources().size ? [...this.selectedSources()] : undefined,
      triggeredBy: this.triggerFilter() || undefined,
      search: this.searchQuery() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
      sortColumn: this.sortColumn(),
      sortDirection: this.sortDirection(),
    }, { silent }).subscribe({
      next: result => {
        this.searching.set(false);
        this.result.set(result);
        this.availableSources.set(result.availableSourceSystemTypes ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.searching.set(false);
        this.loading.set(false);
      },
    });
  }

  onSearch(val: string): void { this.search$.next(val); }

  onStatus(val: string): void  { this.statusFilter.set(val);  this.pageIndex.set(0); this.load(); }
  onTrigger(val: string): void { this.triggerFilter.set(val); this.pageIndex.set(0); this.load(); }

  // ── Source multi-select (mirrors the Workflows list's filter dropdown) ──────
  toggleSourceMenu(): void {
    this.sourceMenuOpen.update(open => !open);
  }

  /** Reads the click's real target rather than scattering stopPropagation() through the template — a click
   *  still inside .filter-dropdown (the trigger, or the open panel that is its DOM descendant) is left alone. */
  @HostListener('document:click', ['$event'])
  closeSourceMenuIfOutside(event: MouseEvent): void {
    if (this.sourceMenuOpen() && !(event.target as HTMLElement).closest('.filter-dropdown')) {
      this.sourceMenuOpen.set(false);
    }
  }

  isSourceSelected(value: string): boolean {
    return this.selectedSources().has(value);
  }

  toggleSourceValue(value: string): void {
    this.selectedSources.update(current => {
      const next = new Set(current);
      if (next.has(value)) next.delete(value); else next.add(value);
      return next;
    });
    this.pageIndex.set(0);
    this.load();
  }

  clearSources(): void {
    this.selectedSources.set(new Set());
    this.pageIndex.set(0);
    this.load();
  }

  reset(): void {
    this.searchQuery.set('');
    this.statusFilter.set('');
    this.selectedSources.set(new Set());
    this.triggerFilter.set('');
    this.pageIndex.set(0);
    this.load();
  }

  /** Leaves the "Execution History" row-action drill-down (see workflowIdFilter) and returns to the
   *  unfiltered, all-workflows list — also strips ?workflowId from the URL so a refresh doesn't restore it. */
  clearWorkflowFilter(): void {
    this.workflowIdFilter.set(null);
    this.pageIndex.set(0);
    this.router.navigate([], { relativeTo: this.route, queryParams: { workflowId: null }, queryParamsHandling: 'merge' });
    this.load();
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  onSort(column: SortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.pageIndex.set(0);
    this.load();
  }

  openDetail(execution: RouteExecution): void {
    this.router.navigate(['/execution-history', execution.id]);
  }

  statusLabel(status: string): string {
    return {
      Pending: 'Pending',
      Running: 'Running',
      // Surfaced as plain "Running": it IS a run still in flight (a node deferred to an async $export job that
      // BulkExportPollWorker will resume), and splitting it out as its own status made the Dashboard's Running
      // tile disagree with what this list showed. The API keeps the distinct AwaitingBulkExport value.
      AwaitingBulkExport: 'Running',
      // validate-run outcomes. Validated is non-terminal ("checked, waiting to run"); Expired is what the
      // sweep turns an abandoned one into. Spelled out rather than left to the ?? fallback, which would show
      // raw PascalCase enum names to the user.
      Validated: 'Validated',
      ValidationFailed: 'Validation Failed',
      Expired: 'Expired',
      Succeeded: 'Succeeded',
      PartialSuccess: 'Partial Success',
      Failed: 'Failed',
      Cancelled: 'Cancelled',
    }[status] ?? status;
  }

  /** Maps this page's RouteExecution status values to the global badge utility classes defined in
   *  styles.scss (badge-completed/-running/-failed/-queued/-inactive) — replaces the page's old local
   *  .status-badge / status-* classes. Cancelled has no dedicated global badge; badge-inactive (muted) is
   *  the closest semantic match. AwaitingBulkExport is non-terminal (a node deferred to an async $export job
   *  and the run is still in flight) so it reuses the Running badge; PartialSuccess is terminal and fully
   *  written (every node ran, only some non-parent resource types were skipped) so it reuses the Succeeded
   *  badge rather than reading as an error. */
  statusClass(status: string): string {
    const map: Record<string, string> = {
      Pending: 'badge-queued',
      Running: 'badge-running',
      AwaitingBulkExport: 'badge-running',
      // Validated is pending-like (nothing has run yet); ValidationFailed is a genuine refusal so it reads as
      // failed; Expired is neither success nor failure — nothing was attempted — so it takes the muted badge
      // Cancelled uses.
      Validated: 'badge-queued',
      ValidationFailed: 'badge-failed',
      Expired: 'badge-inactive',
      Succeeded: 'badge-completed',
      PartialSuccess: 'badge-completed',
      Failed: 'badge-failed',
      Cancelled: 'badge-inactive',
    };
    return map[status] ?? 'badge-inactive';
  }

  triggerClass(triggerType: string | null): string {
    return 'trigger-' + (triggerType ?? 'manual').toLowerCase();
  }

  sourceLabel(execution: RouteExecution): string {
    // Show the vendor type (Epic, Athenahealth, ...) to match the Workflows list's Source badge,
    // not the per-connection name (e.g. "Epic_gogo") — fall back to it only when the type is unknown.
    // Through the same brand-name table that badge uses, or the two drift apart (eCW vs "Healow").
    return sourceSystemDisplayName(execution.sourceSystemType) || execution.sourceName || '—';
  }

  /** Brand name for a Source filter option — same table the Source badge above uses, so the filter's text and
   *  the column's text can't drift (eCW vs "Healow"). The VALUE stays the raw enum the API filters on. */
  sourceOptionLabel(sourceSystemType: string): string {
    return sourceSystemDisplayName(sourceSystemType) || sourceSystemType;
  }

  formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }
}
