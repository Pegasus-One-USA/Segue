import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ToastService } from '../../../services/toast.service';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { NodeRunHistoryEntry, NodeRunPayloadDetail, PagedResult, RouteExecution } from '../../models/execution-history.model';
import { FieldLineagePanelComponent } from '../../components/field-lineage-panel/field-lineage-panel.component';

@Component({
  selector: 'app-execution-history-detail',
  standalone: true,
  imports: [CommonModule, DatePipe, RouterLink, MatIconModule, MatButtonModule, MatPaginatorModule, MatProgressSpinnerModule, FieldLineagePanelComponent],
  templateUrl: './execution-history-detail.component.html',
  styleUrls: ['./execution-history-detail.component.scss'],
})
export class ExecutionHistoryDetailComponent implements OnInit {
  private readonly api = inject(ExecutionHistoryApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly execution   = signal<RouteExecution | null>(null);
  readonly nodeRuns    = signal<PagedResult<NodeRunHistoryEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly loading      = signal(false);
  readonly expandedIds  = signal<Set<string>>(new Set());
  readonly allExpanded  = signal(false);
  /** Fetched lazily on expand, keyed by workflowNodeRunId. A value of null means "fetched, no payload
   *  recorded" (still running, failed before output, or a None-contract node) — distinct from "not fetched
   *  yet," which is simply absent from the map. */
  readonly payloadCache   = signal<Map<string, NodeRunPayloadDetail | null>>(new Map());
  readonly loadingPayloadIds = signal<Set<string>>(new Set());
  readonly pageIndex    = signal(0);
  readonly pageSize     = signal(10);

  readonly activeTab = signal<'nodeRuns' | 'fieldLineage'>('nodeRuns');

  /** Public (not private) — bound into <app-field-lineage-panel [runId]="runId"> once that tab is shown. */
  runId = '';

  ngOnInit(): void {
    this.runId = this.route.snapshot.paramMap.get('id') ?? '';
    if (!this.runId) {
      return;
    }

    this.api.byId(this.runId).subscribe(execution => this.execution.set(execution));
    this.loadNodeRuns();
  }

  setTab(tab: 'nodeRuns' | 'fieldLineage'): void {
    this.activeTab.set(tab);
  }

  loadNodeRuns(): void {
    this.loading.set(true);
    this.api.nodeRuns(this.runId, this.pageIndex() + 1, this.pageSize()).subscribe({
      next: result => {
        this.nodeRuns.set(result);
        this.loading.set(false);
        if (this.allExpanded()) {
          const ids = result.items.map(x => x.workflowNodeRunId);
          this.expandedIds.set(new Set(ids));
          ids.forEach(id => this.loadPayload(id));
        }
      },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadNodeRuns();
  }

  toggleExpanded(entryId: string): void {
    const next = new Set(this.expandedIds());
    if (next.has(entryId)) {
      next.delete(entryId);
    } else {
      next.add(entryId);
      this.loadPayload(entryId);
    }
    this.expandedIds.set(next);
  }

  isExpanded(entryId: string): boolean {
    return this.expandedIds().has(entryId);
  }

  toggleAllExpanded(): void {
    const expandAll = !this.allExpanded();
    this.allExpanded.set(expandAll);
    const ids = this.nodeRuns().items.map(x => x.workflowNodeRunId);
    this.expandedIds.set(expandAll ? new Set(ids) : new Set());
    if (expandAll) {
      ids.forEach(id => this.loadPayload(id));
    }
  }

  /** Fetches one node run's decrypted payload on first expand only — a no-op if it's already cached or a
   *  fetch for it is already in flight, so re-collapsing/re-expanding (or "expand all" re-running) never
   *  refetches. */
  private loadPayload(entryId: string): void {
    if (this.payloadCache().has(entryId) || this.loadingPayloadIds().has(entryId)) {
      return;
    }

    this.loadingPayloadIds.set(new Set(this.loadingPayloadIds()).add(entryId));
    this.api.nodeRunPayload(this.runId, entryId).subscribe({
      next: detail => this.payloadCache.set(new Map(this.payloadCache()).set(entryId, detail)),
      error: () => this.payloadCache.set(new Map(this.payloadCache()).set(entryId, null)),
      complete: () => {
        const next = new Set(this.loadingPayloadIds());
        next.delete(entryId);
        this.loadingPayloadIds.set(next);
      },
    });
  }

  isLoadingPayload(entryId: string): boolean {
    return this.loadingPayloadIds().has(entryId);
  }

  /** The decrypted payload JSON for an expanded row, once loadPayload resolves — null before it resolves or
   *  when the node run genuinely has none recorded. */
  payloadJsonFor(entryId: string): string | null {
    return this.payloadCache().get(entryId)?.payloadJson ?? null;
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

  contractClass(contract: string | null): string {
    return 'contract-' + (contract ?? 'none').toLowerCase();
  }

  copyCorrelationId(): void {
    const correlationId = this.execution()?.correlationId;
    if (!correlationId) { return; }

    navigator.clipboard.writeText(correlationId).then(
      () => this.toast.show('Copied', 'Correlation ID copied to clipboard.'),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }

  /** Copies whatever's actually shown in the expanded panel for this node — the formatted payload on
   *  success, or the error message when it failed/was cancelled — so there's always something sensible to
   *  copy regardless of outcome. */
  copyNodeDetail(entry: NodeRunHistoryEntry): void {
    const payloadJson = this.payloadJsonFor(entry.workflowNodeRunId);
    const text = payloadJson ? this.formatJson(payloadJson) : (entry.errorMessage ?? '');
    if (!text) { return; }

    navigator.clipboard.writeText(text).then(
      () => this.toast.show('Copied', `${entry.nodeType} details copied to clipboard.`),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }
}
