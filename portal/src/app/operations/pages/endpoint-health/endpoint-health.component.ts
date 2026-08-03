import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { OperationsApiService } from '../../services/operations-api.service';
import { EndpointHealthCheckEntry, PagedResult } from '../../models/operations.model';

@Component({
  selector: 'app-endpoint-health',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule],
  templateUrl: './endpoint-health.component.html',
  styleUrl: './endpoint-health.component.scss',
})
export class EndpointHealthComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly result = signal<PagedResult<EndpointHealthCheckEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly displayedCols = ['occurredOnUtc', 'endpointName', 'endpointType', 'status', 'latencyMs', 'message'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.endpointHealth(this.pageIndex() + 1, this.pageSize()).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  statusClass(status: string): string {
    return status === 'Healthy' ? 'status-success' : 'status-fail';
  }
}
