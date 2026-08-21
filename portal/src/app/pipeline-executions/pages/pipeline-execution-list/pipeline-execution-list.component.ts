import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { PipelineExecutionApiService } from '../../services/pipeline-execution-api.service';
import { PipelineExecutionEntry } from '../../models/pipeline-execution.model';

@Component({
  selector: 'app-pipeline-execution-list',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, MatPaginatorModule],
  templateUrl: './pipeline-execution-list.component.html',
  styleUrl: './pipeline-execution-list.component.scss',
})
export class PipelineExecutionListComponent implements OnInit {
  private readonly api = inject(PipelineExecutionApiService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly entries = signal<PipelineExecutionEntry[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly perPage = signal(10);

  readonly search = signal('');
  readonly status = signal('');

  readonly displayedCols = ['pipelineName', 'sourceName', 'startedOnUtc', 'durationMs', 'status', 'triggeredBy', 'counts', 'correlationId'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.list(this.page(), this.perPage(), this.status() || undefined, undefined, undefined, this.search() || undefined).subscribe({
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

  onPageChange(e: PageEvent): void {
    this.page.set(e.pageIndex + 1);
    this.perPage.set(e.pageSize);
    this.load();
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
