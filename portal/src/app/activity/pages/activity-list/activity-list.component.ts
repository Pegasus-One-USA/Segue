import { Component, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { DashboardService } from '../../../dashboard/services/dashboard.service';
import { ActivityEvent, ActivitySeverity } from '../../../dashboard/models/activity-event.model';

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
export class ActivityListComponent {
  private readonly dashSvc = inject(DashboardService);

  readonly searchQuery     = signal('');
  readonly severityFilter  = signal('');
  readonly pageIndex       = signal(0);
  readonly pageSize        = signal(10);

  readonly displayedCols = ['index', 'severity', 'message', 'timestamp'];

  readonly filtered = computed(() => {
    const q  = this.searchQuery().toLowerCase().trim();
    const sv = this.severityFilter();
    return this.dashSvc.activity().filter(e => {
      const matchQ  = !q  || e.message.toLowerCase().includes(q);
      const matchSv = !sv || e.severity === sv;
      return matchQ && matchSv;
    });
  });

  readonly paginated = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  readonly showingFrom = computed(() =>
    this.filtered().length === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.filtered().length)
  );

  onSearch(val: string): void   { this.searchQuery.set(val);    this.pageIndex.set(0); }
  onSeverity(val: string): void { this.severityFilter.set(val); this.pageIndex.set(0); }

  reset(): void {
    this.searchQuery.set('');
    this.severityFilter.set('');
    this.pageIndex.set(0);
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  severityLabel(s: ActivitySeverity): string {
    return { info: 'Info', warning: 'Warning', error: 'Error', success: 'Success' }[s];
  }

  severityIcon(s: ActivitySeverity): string {
    return { info: 'ℹ', warning: '⚠', error: '✕', success: '✓' }[s];
  }
}
