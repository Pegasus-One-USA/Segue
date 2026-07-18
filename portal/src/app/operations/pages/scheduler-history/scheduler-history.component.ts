import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { SchedulerHistoryEntry } from '../../models/operations.model';

@Component({
  selector: 'app-scheduler-history',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './scheduler-history.component.html',
  styleUrl: './scheduler-history.component.scss',
})
export class SchedulerHistoryComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<SchedulerHistoryEntry[]>([]);

  readonly displayedCols = ['runTimeUtc', 'schedulerId', 'status', 'routeCount', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.schedulerHistory(this.correlationId() || undefined).subscribe({
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
