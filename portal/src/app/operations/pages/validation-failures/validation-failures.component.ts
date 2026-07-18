import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ValidationFailureEntry } from '../../models/operations.model';

@Component({
  selector: 'app-validation-failures',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './validation-failures.component.html',
  styleUrl: './validation-failures.component.scss',
})
export class ValidationFailuresComponent implements OnInit {
  private readonly api = inject(OperationsApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<ValidationFailureEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'resourceType', 'warnings', 'dataQualityScore', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.validationFailures(this.correlationId() || undefined).subscribe({
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

  parseWarnings(warningsJson: string): string[] {
    try {
      return JSON.parse(warningsJson);
    } catch {
      return [warningsJson];
    }
  }
}
