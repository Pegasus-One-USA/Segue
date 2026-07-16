import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { OperationalLogsApiService } from '../../services/operational-logs-api.service';
import { OperationalLog, PagedResult } from '../../models/operational-log.model';

@Component({
  selector: 'app-operational-logs-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './operational-logs-list.component.html',
  styleUrls: ['./operational-logs-list.component.scss'],
})
export class OperationalLogsListComponent implements OnInit, OnDestroy {
  private readonly api = inject(OperationalLogsApiService);
  private readonly search$ = new Subject<string>();
  private readonly status$ = new Subject<string>();
  private readonly action$ = new Subject<string>();

  readonly searchQuery = signal('');
  readonly statusFilter = signal('');
  readonly actionFilter = signal('');
  readonly severityFilter = signal('');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly loading = signal(false);
  readonly result = signal<PagedResult<OperationalLog>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });

  readonly displayedCols = ['index', 'severity', 'action', 'resourceType', 'status', 'message', 'triggeredBy', 'occurredOnUtc'];

  ngOnInit(): void {
    this.search$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.searchQuery.set(value);
      this.pageIndex.set(0);
      this.load();
    });
    this.status$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.statusFilter.set(value);
      this.pageIndex.set(0);
      this.load();
    });
    this.action$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.actionFilter.set(value);
      this.pageIndex.set(0);
      this.load();
    });

    this.load();
  }

  ngOnDestroy(): void {
    this.search$.complete();
    this.status$.complete();
    this.action$.complete();
  }

  load(): void {
    this.loading.set(true);
    this.api.list({
      status: this.statusFilter() || undefined,
      action: this.actionFilter() || undefined,
      severity: this.severityFilter() || undefined,
      search: this.searchQuery() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: result => {
        this.result.set(result);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onSearch(val: string): void { this.search$.next(val); }
  onStatusInput(val: string): void { this.status$.next(val); }
  onActionInput(val: string): void { this.action$.next(val); }
  onSeverity(val: string): void { this.severityFilter.set(val); this.pageIndex.set(0); this.load(); }

  reset(): void {
    this.searchQuery.set('');
    this.statusFilter.set('');
    this.actionFilter.set('');
    this.severityFilter.set('');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  readonly showingFrom = () =>
    this.result().totalCount === 0 ? 0 : this.pageIndex() * this.pageSize() + 1;

  readonly showingTo = () =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.result().totalCount);

  statusClass(status: string): string {
    return 'status-' + status.toLowerCase();
  }

  severityClass(severity: string): string {
    return 'severity-' + severity.toLowerCase();
  }
}
