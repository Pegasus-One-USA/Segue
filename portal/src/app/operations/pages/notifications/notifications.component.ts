import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { OperationsApiService } from '../../services/operations-api.service';
import { NotificationHistoryEntry, PagedResult } from '../../models/operations.model';

@Component({
  selector: 'app-notifications',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule],
  templateUrl: './notifications.component.html',
  styleUrl: './notifications.component.scss',
})
export class NotificationsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly result = signal<PagedResult<NotificationHistoryEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly displayedCols = ['occurredOnUtc', 'notificationType', 'recipient', 'subject', 'status', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.notifications(this.correlationId() || undefined, this.pageIndex() + 1, this.pageSize()).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  reset(): void {
    this.correlationId.set('');
    this.pageIndex.set(0);
    this.load();
  }
}
