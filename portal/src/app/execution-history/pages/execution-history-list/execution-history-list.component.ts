import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { PagedResult, RouteExecution } from '../../models/execution-history.model';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

type SortColumn = 'pipeline' | 'source' | 'status' | 'duration' | 'lastRun' | 'triggeredBy';
type SortDirection = 'asc' | 'desc';

@Component({
  selector: 'app-execution-history-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
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
  private readonly search$ = new Subject<string>();

  readonly searchQuery   = signal('');
  readonly statusFilter  = signal('');
  readonly triggerFilter = signal('');
  readonly pageIndex     = signal(0);
  readonly pageSize      = signal(10);
  readonly sortColumn    = signal<SortColumn>('lastRun');
  readonly sortDirection = signal<SortDirection>('desc');
  readonly loading       = signal(false);
  readonly result        = signal<PagedResult<RouteExecution>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });

  readonly displayedCols = ['index', 'name', 'source', 'status', 'duration', 'lastRun', 'triggeredBy'];

  ngOnInit(): void {
    this.search$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.searchQuery.set(value);
      this.pageIndex.set(0);
      this.load();
    });

    this.load();
  }

  ngOnDestroy(): void {
    this.search$.complete();
  }

  load(): void {
    this.loading.set(true);
    this.api.list({
      status: this.statusFilter() || undefined,
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
  onTrigger(val: string): void { this.triggerFilter.set(val); this.pageIndex.set(0); this.load(); }

  reset(): void {
    this.searchQuery.set('');
    this.statusFilter.set('');
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
      Succeeded: 'Succeeded',
      Failed: 'Failed',
      Cancelled: 'Cancelled',
    }[status] ?? status;
  }

  statusClass(status: string): string {
    return 'status-' + status.toLowerCase();
  }

  triggerClass(triggerType: string | null): string {
    return 'trigger-' + (triggerType ?? 'manual').toLowerCase();
  }

  sourceLabel(execution: RouteExecution): string {
    return execution.sourceName ?? execution.sourceSystemType ?? '—';
  }

  formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }
}
