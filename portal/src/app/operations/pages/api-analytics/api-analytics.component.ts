import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ApiAnalytics } from '../../models/operations.model';

@Component({
  selector: 'app-api-analytics',
  standalone: true,
  imports: [CommonModule, MatTableModule],
  templateUrl: './api-analytics.component.html',
  styleUrl: './api-analytics.component.scss',
})
export class ApiAnalyticsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly analytics = signal<ApiAnalytics | null>(null);

  readonly displayedCols = ['method', 'url', 'callCount', 'averageDurationMs', 'p95DurationMs', 'errorCount'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.apiAnalytics().subscribe({
      next: analytics => { this.analytics.set(analytics); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
