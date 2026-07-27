import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { PipelineExecutionApiService } from '../../services/pipeline-execution-api.service';
import { PipelineExecutionEntry, PipelineResourceHistoryEntry } from '../../models/pipeline-execution.model';

interface ResourceRow {
  entry: PipelineResourceHistoryEntry;
  status: string;
  processingTimeMs: number | null;
  note: string;
}

function toRow(entry: PipelineResourceHistoryEntry): ResourceRow {
  const completedAt = entry.storedAtUtc ?? entry.mappedAtUtc ?? entry.normalizedAtUtc ?? null;
  const processingTimeMs = completedAt
    ? new Date(completedAt).getTime() - new Date(entry.fetchedAtUtc).getTime()
    : null;

  const status = entry.errorMessage ? 'Failed' : entry.writeStatus ?? entry.stage;
  const note = entry.errorMessage ?? (entry.warnings.length > 0 ? entry.warnings.join('; ') : '—');

  return { entry, status, processingTimeMs, note };
}

@Component({
  selector: 'app-pipeline-execution-detail',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, RouterLink],
  templateUrl: './pipeline-execution-detail.component.html',
  styleUrl: './pipeline-execution-detail.component.scss',
})
export class PipelineExecutionDetailComponent implements OnInit {
  private readonly api = inject(PipelineExecutionApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly execution = signal<PipelineExecutionEntry | null>(null);
  readonly rows = signal<ResourceRow[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly pageSize = 25;

  readonly displayedCols = ['resourceType', 'sourceResourceId', 'stage', 'status', 'processingTimeMs', 'dataQuality', 'patientMatch', 'note', 'lineage'];

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;

    this.loading.set(true);
    this.api.byId(id).subscribe({ next: execution => this.execution.set(execution) });
    this.loadResources(id);
  }

  private loadResources(id: string): void {
    this.loading.set(true);
    this.api.resources(id, this.page(), this.pageSize).subscribe({
      next: result => {
        this.rows.set(result.items.map(toRow));
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  nextPage(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (id && this.page() * this.pageSize < this.totalCount()) {
      this.page.set(this.page() + 1);
      this.loadResources(id);
    }
  }

  previousPage(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (id && this.page() > 1) {
      this.page.set(this.page() - 1);
      this.loadResources(id);
    }
  }

  goBack(): void {
    this.router.navigate(['/operations/pipeline-executions']);
  }

  viewCorrelation(): void {
    const correlationId = this.execution()?.correlationId;
    if (correlationId) {
      this.router.navigate(['/governance/correlation-search'], { queryParams: { correlationId } });
    }
  }
}
