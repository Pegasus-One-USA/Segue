import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { PipelineExecutionApiService } from '../../services/pipeline-execution-api.service';
import { PipelineExecutionEntry } from '../../models/pipeline-execution.model';

@Component({
  selector: 'app-pipeline-execution-list',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './pipeline-execution-list.component.html',
  styleUrl: './pipeline-execution-list.component.scss',
})
export class PipelineExecutionListComponent implements OnInit {
  private readonly api = inject(PipelineExecutionApiService);
  private readonly router = inject(Router);

  protected readonly Math = Math;

  readonly loading = signal(false);
  readonly entries = signal<PipelineExecutionEntry[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly pageSize = 25;

  readonly search = signal('');
  readonly status = signal('');

  readonly displayedCols = ['pipelineName', 'sourceName', 'startedOnUtc', 'durationMs', 'status', 'triggeredBy', 'counts', 'correlationId'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.list(this.page(), this.pageSize, this.status() || undefined, undefined, undefined, this.search() || undefined).subscribe({
      next: result => {
        this.entries.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onSearchChange(value: string): void {
    this.search.set(value);
  }

  onStatusChange(value: string): void {
    this.status.set(value);
  }

  applyFilters(): void {
    this.page.set(1);
    this.load();
  }

  reset(): void {
    this.search.set('');
    this.status.set('');
    this.page.set(1);
    this.load();
  }

  nextPage(): void {
    if (this.page() * this.pageSize < this.totalCount()) {
      this.page.set(this.page() + 1);
      this.load();
    }
  }

  previousPage(): void {
    if (this.page() > 1) {
      this.page.set(this.page() - 1);
      this.load();
    }
  }

  openDetail(entry: PipelineExecutionEntry): void {
    this.router.navigate(['/operations/pipeline-executions', entry.id]);
  }

  viewCorrelation(entry: PipelineExecutionEntry, event: Event): void {
    event.stopPropagation();
    if (entry.correlationId) {
      this.router.navigate(['/governance/correlation-search'], { queryParams: { correlationId: entry.correlationId } });
    }
  }
}
