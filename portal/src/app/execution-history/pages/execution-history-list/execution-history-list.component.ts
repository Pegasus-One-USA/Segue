import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { PagedResult, RouteExecution } from '../../models/execution-history.model';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

type SortColumn = 'pipeline' | 'source' | 'status' | 'duration' | 'lastRun' | 'triggeredBy';
type SortDirection = 'asc' | 'desc';

/** Options for the Source filter dropdown — the SourceSystemType enum's raw values (what the backend's
 *  `source` query param and each row's sourceSystemType actually carry) paired with a friendly label,
 *  same vendor set as sources.data.ts (the Source Connections picker). */
const SOURCE_FILTER_OPTIONS: { value: string; label: string }[] = [
  { value: 'Epic',               label: 'Epic' },
  { value: 'Cerner',              label: 'Cerner (Oracle Health)' },
  { value: 'Athenahealth',        label: 'Athenahealth' },
  { value: 'Allscripts',          label: 'Allscripts (Veradigm)' },
  { value: 'Healow',              label: 'Healow (eClinicalWorks)' },
  { value: 'MeditechGreenfield',  label: 'Meditech Greenfield' },
  { value: 'GenericFhir',         label: 'Generic FHIR' },
  { value: 'Hl7v2',               label: 'HL7 v2 / MLLP' },
  { value: 'Sample',              label: 'Sample (sandbox)' },
];

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

  readonly sourceOptions = SOURCE_FILTER_OPTIONS;

  readonly searchQuery   = signal('');
  readonly statusFilter  = signal('');
  readonly sourceFilter  = signal('');
  readonly triggerFilter = signal('');
  readonly pageIndex     = signal(0);
  readonly pageSize      = signal(10);
  readonly sortColumn    = signal<SortColumn>('lastRun');
  readonly sortDirection = signal<SortDirection>('desc');
  readonly loading       = signal(false);
  readonly result        = signal<PagedResult<RouteExecution>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });

  readonly displayedCols = ['name', 'source', 'status', 'duration', 'lastRun', 'triggeredBy', 'correlationId'];

  ngOnInit(): void {
    this.search$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.searchQuery.set(value);
      this.pageIndex.set(0);
      this.load();
    });

    // A Dashboard stat tile links here with ?status=X (e.g. clicking "Failed") — pre-select that
    // status filter before the first load so the tile's click-through actually lands pre-filtered.
    const statusFromQuery = this.route.snapshot.queryParamMap.get('status');
    if (statusFromQuery) {
      this.statusFilter.set(statusFromQuery);
    }

    this.load();
  }

  ngOnDestroy(): void {
    this.search$.complete();
  }

  load(): void {
    this.loading.set(true);
    this.api.list({
      status: this.statusFilter() || undefined,
      source: this.sourceFilter() || undefined,
      triggeredBy: this.triggerFilter() || undefined,
      search: this.searchQuery() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
      sortColumn: this.sortColumn(),
      sortDirection: this.sortDirection(),
    }).subscribe({
      next: result => {
        this.result.set(result);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onSearch(val: string): void { this.search$.next(val); }

  onStatus(val: string): void  { this.statusFilter.set(val);  this.pageIndex.set(0); this.load(); }
  onSource(val: string): void  { this.sourceFilter.set(val);  this.pageIndex.set(0); this.load(); }
  onTrigger(val: string): void { this.triggerFilter.set(val); this.pageIndex.set(0); this.load(); }

  reset(): void {
    this.searchQuery.set('');
    this.statusFilter.set('');
    this.sourceFilter.set('');
    this.triggerFilter.set('');
    this.pageIndex.set(0);
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
    return execution.sourceSystemType ?? execution.sourceName ?? '—';
  }

  formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }
}
