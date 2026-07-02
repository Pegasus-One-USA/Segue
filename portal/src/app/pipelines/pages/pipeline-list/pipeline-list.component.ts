import { Component, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { PipelineRunService } from '../../../dashboard/services/pipeline-run.service';
import { PipelineRun, PipelineRunStatus } from '../../../dashboard/models/pipeline-run.model';

@Component({
  selector: 'app-pipeline-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './pipeline-list.component.html',
  styleUrls: ['./pipeline-list.component.scss'],
})
export class PipelineListComponent {
  private readonly runSvc = inject(PipelineRunService);

  readonly searchQuery   = signal('');
  readonly statusFilter  = signal('');
  readonly triggerFilter = signal('');
  readonly pageIndex     = signal(0);
  readonly pageSize      = signal(10);

  readonly displayedCols = ['index', 'name', 'source', 'status', 'duration', 'lastRun', 'triggeredBy'];

  readonly filtered = computed(() => {
    const q   = this.searchQuery().toLowerCase().trim();
    const st  = this.statusFilter();
    const tr  = this.triggerFilter();
    return this.runSvc.runs().filter(r => {
      const matchQ  = !q  || r.name.toLowerCase().includes(q) || r.sourceType.toLowerCase().includes(q);
      const matchSt = !st || r.status === st;
      const matchTr = !tr || r.triggeredBy === tr;
      return matchQ && matchSt && matchTr;
    });
  });

  readonly paginated = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  readonly showingFrom = computed(() =>
    this.filtered().length === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.filtered().length)
  );

  onSearch(val: string): void  { this.searchQuery.set(val);   this.pageIndex.set(0); }
  onStatus(val: string): void  { this.statusFilter.set(val);  this.pageIndex.set(0); }
  onTrigger(val: string): void { this.triggerFilter.set(val); this.pageIndex.set(0); }

  reset(): void {
    this.searchQuery.set('');
    this.statusFilter.set('');
    this.triggerFilter.set('');
    this.pageIndex.set(0);
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  statusLabel(s: PipelineRunStatus): string {
    return { running: 'Running', completed: 'Completed', failed: 'Failed', queued: 'Queued', cancelled: 'Cancelled' }[s];
  }

  formatDuration(ms?: number): string {
    if (!ms) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }
}
