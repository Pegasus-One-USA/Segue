import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { UserActivityLogsApiService } from '../../services/user-activity-logs-api.service';
import { PagedResult, UserActivityLog } from '../../models/user-activity-log.model';

@Component({
  selector: 'app-activity-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './activity-list.component.html',
  styleUrls: ['./activity-list.component.scss'],
})
export class ActivityListComponent implements OnInit, OnDestroy {
  private readonly api = inject(UserActivityLogsApiService);
  private readonly search$ = new Subject<string>();

  readonly searchQuery    = signal('');
  readonly categoryFilter = signal('');
  readonly statusFilter   = signal('');
  readonly pageIndex      = signal(0);
  readonly pageSize       = signal(10);
  readonly loading        = signal(false);
  readonly result         = signal<PagedResult<UserActivityLog>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });

  readonly displayedCols = ['index', 'category', 'activity', 'status', 'user', 'occurredOnUtc'];

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
      category: this.categoryFilter() || undefined,
      status: this.statusFilter() || undefined,
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

  onCategory(val: string): void { this.categoryFilter.set(val); this.pageIndex.set(0); this.load(); }
  onStatus(val: string): void   { this.statusFilter.set(val);   this.pageIndex.set(0); this.load(); }

  reset(): void {
    this.searchQuery.set('');
    this.categoryFilter.set('');
    this.statusFilter.set('');
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

  userLabel(entry: UserActivityLog): string {
    return entry.userEmail || '—';
  }
}
