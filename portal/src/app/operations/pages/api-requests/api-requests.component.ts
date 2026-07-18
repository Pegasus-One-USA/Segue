import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ApiRequestLogEntry } from '../../models/operations.model';

@Component({
  selector: 'app-api-requests',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './api-requests.component.html',
  styleUrl: './api-requests.component.scss',
})
export class ApiRequestsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<ApiRequestLogEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'method', 'url', 'statusCode', 'durationMs', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.apiRequests(this.correlationId() || undefined).subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  reset(): void {
    this.correlationId.set('');
    this.load();
  }

  statusClass(statusCode: number | null): string {
    if (statusCode === null) return 'status-fail';
    return statusCode >= 200 && statusCode < 400 ? 'status-success' : 'status-fail';
  }
}
