import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { PagedResult, ResourceHistoryEntry, RouteExecution } from '../../models/execution-history.model';

@Component({
  selector: 'app-execution-history-detail',
  standalone: true,
  imports: [CommonModule, DatePipe, MatIconModule, MatButtonModule, MatPaginatorModule],
  templateUrl: './execution-history-detail.component.html',
  styleUrls: ['./execution-history-detail.component.scss'],
})
export class ExecutionHistoryDetailComponent implements OnInit {
  private readonly api = inject(ExecutionHistoryApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly execution   = signal<RouteExecution | null>(null);
  readonly resources   = signal<PagedResult<ResourceHistoryEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly loading      = signal(false);
  readonly expandedId   = signal<string | null>(null);
  readonly pageIndex    = signal(0);
  readonly pageSize     = signal(25);

  private runId = '';

  ngOnInit(): void {
    this.runId = this.route.snapshot.paramMap.get('id') ?? '';
    if (!this.runId) {
      return;
    }

    this.api.byId(this.runId).subscribe(execution => this.execution.set(execution));
    this.loadResources();
  }

  loadResources(): void {
    this.loading.set(true);
    this.api.resources(this.runId, this.pageIndex() + 1, this.pageSize()).subscribe({
      next: result => {
        this.resources.set(result);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadResources();
  }

  toggleExpanded(entryId: string): void {
    this.expandedId.set(this.expandedId() === entryId ? null : entryId);
  }

  back(): void {
    this.router.navigate(['/execution-history']);
  }

  statusClass(status: string): string {
    return 'status-' + status.toLowerCase();
  }

  formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }

  /** This is the workflow run's own id, NOT the ErrorLogs ErrorReferenceId (the "ERR-YYYYMMDD-NNNNNN" a support
   *  engineer looks up in Operations → Errors) — labeled "Execution ID" rather than "Reference" so it's not
   *  mistaken for that different, unrelated identifier. Searchable in Operations → Errors via its "Execution ID"
   *  filter, which resolves to the matching ErrorLogs row and its real ErrorReferenceId. */
  errorDisplayMessage(): string {
    return `Something went wrong while running this workflow. Please contact your admin. (Execution ID: ${this.runId})`;
  }

  formatJson(value: string | null): string {
    if (!value) return '';
    try {
      return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      return value;
    }
  }

  contractClass(contract: string): string {
    return 'contract-' + contract.toLowerCase();
  }
}
