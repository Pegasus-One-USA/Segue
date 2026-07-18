import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { RetryHistoryEntry } from '../../models/operations.model';

@Component({
  selector: 'app-retry-history',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './retry-history.component.html',
  styleUrl: './retry-history.component.scss',
})
export class RetryHistoryComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<RetryHistoryEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'context', 'retryNumber', 'delayMilliseconds', 'reason', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.retryHistory(this.correlationId() || undefined).subscribe({
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
}
