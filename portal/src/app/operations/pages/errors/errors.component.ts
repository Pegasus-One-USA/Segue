import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ErrorLogEntry } from '../../models/operations.model';

@Component({
  selector: 'app-errors',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './errors.component.html',
  styleUrl: './errors.component.scss',
})
export class ErrorsComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<ErrorLogEntry[]>([]);
  readonly expandedId = signal<string | null>(null);

  readonly displayedCols = ['occurredOnUtc', 'severity', 'module', 'exceptionType', 'message', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.errors(this.correlationId() || undefined).subscribe({
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

  toggleStackTrace(id: string): void {
    this.expandedId.set(this.expandedId() === id ? null : id);
  }

  severityClass(severity: string): string {
    return 'severity-' + severity.toLowerCase();
  }
}
