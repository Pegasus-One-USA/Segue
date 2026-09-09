import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import { DestinationWriteResultPayload, NodeRunHistoryEntry, NodeRunPayloadDetail, PagedResult, RouteExecution } from '../../models/execution-history.model';
import { FieldLineagePanelComponent } from '../../components/field-lineage-panel/field-lineage-panel.component';

@Component({
  selector: 'app-execution-history-detail',
  standalone: true,
  imports: [CommonModule, DatePipe, RouterLink, MatIconModule, MatButtonModule, MatPaginatorModule, MatProgressSpinnerModule, FieldLineagePanelComponent],
  templateUrl: './execution-history-detail.component.html',
  styleUrls: ['./execution-history-detail.component.scss'],
})
export class ExecutionHistoryDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(ExecutionHistoryApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly execution   = signal<RouteExecution | null>(null);
  readonly cancelling  = signal(false);
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

  ngOnDestroy(): void {
    if (this.cancelPollTimer !== null) {
      clearTimeout(this.cancelPollTimer);
    }
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

  /** Parses a DestinationWriteResult-contract node's payload so the template can render a download link / email
   *  delivery card instead of raw JSON. Returns null for every other contract, or if the payload has neither a
   *  download link nor email delivery detail to show (e.g. a plain SQL/Blob write — just a record count). */
  destinationWriteResultFor(entry: NodeRunHistoryEntry): DestinationWriteResultPayload | null {
    if (entry.contract !== 'DestinationWriteResult') return null;
    const payloadJson = this.payloadJsonFor(entry.workflowNodeRunId);
    if (!payloadJson) return null;

    try {
      const parsed = JSON.parse(payloadJson) as DestinationWriteResultPayload;
      return parsed.DownloadUrl || parsed.EmailDelivery ? parsed : null;
    } catch {
      return null;
    }
  }

  back(): void {
    this.router.navigate(['/execution-history']);
  }

  /** Requests a graceful stop — the node currently in flight finishes normally, no further nodes start, and
   *  the run settles into Cancelled. Steps already completed are NOT undone; a re-run can double-write at any
   *  destination that isn't configured for Upsert (plain SQL insert, file/blob writers), so the confirmation
   *  says so plainly rather than implying a clean rollback that doesn't exist. */
  cancelRun(): void {
    this.dialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '480px',
        data: {
          title: 'Cancel this run?',
          message: 'This stops the workflow after its current step finishes. Steps already completed are not undone — re-running later may duplicate data at destinations not configured for Upsert (plain SQL insert, file/blob writers).',
          confirmLabel: 'Cancel Run',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;

        this.cancelling.set(true);
        this.api.cancel(this.runId).subscribe({
          next: () => {
            this.toast.show('Cancellation requested', 'The run will stop once its current step finishes.');
            this.pollUntilSettled();
          },
          error: (err: HttpErrorResponse) => {
            this.cancelling.set(false);
            const message = err.status === 409
              ? 'This run has already finished and cannot be cancelled.'
              : 'Failed to request cancellation. Please try again.';
            this.toast.error(message);
          },
        });
      });
  }

  private cancelPollTimer: ReturnType<typeof setTimeout> | null = null;

  /** Polls this run's status while a cancel is pending — there's no live push on this page (unlike the
   *  Dashboard/Workflow List's SignalR-driven refresh), so without this the badge would keep reading
   *  "Running" until the user manually reloads, even once the run has actually settled into Cancelled. */
  private pollUntilSettled(): void {
    this.api.byId(this.runId).subscribe(execution => {
      this.execution.set(execution);
      if (execution.status === 'Running') {
        this.cancelPollTimer = setTimeout(() => this.pollUntilSettled(), 3000);
        return;
      }

      this.cancelling.set(false);
      this.loadNodeRuns();
    });
  }

  statusClass(status: string): string {
    return 'status-' + status.toLowerCase();
  }

  /** Same friendly labels as execution-history-list.component.ts's statusLabel() — kept in sync so a run's
   *  status reads the same on both screens. */
  statusLabel(status: string): string {
    return {
      Pending: 'Pending',
      Running: 'Running',
      // Shown as plain "Running" — same reasoning as ExecutionHistoryListComponent.statusLabel.
      AwaitingBulkExport: 'Running',
      Succeeded: 'Succeeded',
      PartialSuccess: 'Partial Success',
      Failed: 'Failed',
      Cancelled: 'Cancelled',
    }[status] ?? status;
  }

  formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms < 60000) return `${Math.round(ms / 1000)}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }

  /** Fallback only — used when this run's ErrorReferenceId is null (no capture ran, or the capture itself
   *  failed to persist; see the backend's GlobalExceptionManager remarks). The template links straight to
   *  Operations → Errors via ErrorReferenceId when one is present, so this text-only path is now the exception,
   *  not the default. Shows the run's own Execution ID (not an ErrorLogs ErrorReferenceId) as a last-resort
   *  manual lookup key, findable in Operations → Errors via its "Execution ID" filter. */
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

  /** One glyph per pipeline stage's output contract — mirrors the Runtime Plane topology (extraction →
   *  governance → transform → output → audit) so the node marker reads as "what kind of step is this"
   *  at a glance, not just a generic bolt. */
  private static readonly CONTRACT_ICONS: Record<string, string> = {
    resourcebatch: 'download',
    normalizedresourcebatch: 'rule',
    mappedrecordbatch: 'sync_alt',
    deidentifiedbatch: 'shield',
    destinationwriteresult: 'upload',
    auditresult: 'fact_check',
  };

  contractIcon(contract: string | null): string {
    return ExecutionHistoryDetailComponent.CONTRACT_ICONS[(contract ?? '').toLowerCase()] ?? 'bolt';
  }

  /** A source node run now carries its vendor's own NodeType for every vendor the backend catalog accepts
   *  (Epic, athenahealth, eClinicalWorks, generic FHIR, Sample). Two cases still arrive as the shared
   *  'EpicSourceNode': a run of a workflow saved before per-vendor node types existed (those rows are not
   *  migrated), and a vendor still gated out of that catalog (Cerner/Allscripts/Meditech). Extraction was
   *  always vendor-correct either way — SourceNodeExecutors.cs resolves the real HTTP client from the
   *  connection's SourceSystemType — so this stays a display-only fix: label a source node from this run's
   *  real configured source, so those older runs don't read as "EpicSourceNode" either. Same sourceName-first
   *  fallback the header above already uses. */
  nodeLabel(entry: NodeRunHistoryEntry): string {
    if (!entry.nodeType.endsWith('SourceNode')) {
      return entry.nodeType;
    }
    const source = this.execution()?.sourceName ?? this.execution()?.sourceSystemType;
    return source ? `${source} Source` : entry.nodeType;
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
      () => this.toast.show('Copied', `${this.nodeLabel(entry)} details copied to clipboard.`),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }
}
