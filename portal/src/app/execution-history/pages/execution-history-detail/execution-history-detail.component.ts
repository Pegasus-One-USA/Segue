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
import { DestinationWriteResultPayload, NodeRunHistoryEntry, NodeRunPayloadDetail, PagedResult, RouteExecution,
  NodeLineageBreakdown,
  LineageRuleCount,
  LineageResourceTypeCount,
  ConfiguredResourceTypeRules,
} from '../../models/execution-history.model';
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
  /** Per-node field-lineage breakdown, keyed by DEFINITION node id (not node-run id) — what each node
   *  actually applied. Fetched once per run alongside the node list. */
  readonly nodeBreakdowns = signal<Map<string, NodeLineageBreakdown>>(new Map());
  /** Transformation rules CONFIGURED on this run's workflow, by resource type. Configuration rather than
   *  runtime lineage, so it belongs to the workflow itself and is shown against the transform node. */
  readonly configuredRules = signal<ConfiguredResourceTypeRules[]>([]);
  /** De-identification rules configured for this run, by resource type — the de-identification node's
   *  equivalent of configuredRules. */
  readonly configuredDeIdRules = signal<ConfiguredResourceTypeRules[]>([]);
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
    // Independent of the node list and deliberately not blocking it: a run with no recorded lineage (or an
    // older run from before lineage was captured) simply shows resource counts, exactly as before.
    this.api.lineageNodeBreakdown(this.runId).subscribe({
      next: breakdowns => this.nodeBreakdowns.set(new Map(breakdowns.map(b => [b.workflowNodeId, b]))),
      error: () => this.nodeBreakdowns.set(new Map()),
    });
    this.api.configuredRules(this.runId).subscribe({
      next: rules => this.configuredRules.set(rules),
      error: () => this.configuredRules.set([]),
    });
    this.api.configuredDeIdRules(this.runId).subscribe({
      next: rules => this.configuredDeIdRules.set(rules),
      error: () => this.configuredDeIdRules.set([]),
    });
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

  /** Per-resource-type counts for an expanded row, once loadPayload resolves — e.g. [['Patient', 1],
   *  ['Observation', 42]]. Node OUTPUT is no longer retained (it was raw Epic FHIR), so an expanded row
   *  reports how much the node produced rather than what it contained. */
  resourceCountsFor(entryId: string): [string, number][] {
    const json = this.payloadCache().get(entryId)?.resourceTypeCountsJson;
    if (!json) return [];
    try {
      return Object.entries(JSON.parse(json) as Record<string, number>);
    } catch {
      return [];
    }
  }

  /** A destination node's delivery detail (download link / email envelope) for the expanded row.
   *
   *  This used to be parsed out of the node's stored output. That store held whole Epic FHIR resources and is
   *  gone, so the delivery metadata — which is NOT resource content — is now persisted on its own alongside the
   *  counts, and read from there. Returns null for every other contract, or when there is neither a download
   *  link nor email delivery to show (e.g. a plain SQL write — just a record count). */
  destinationWriteResultFor(entry: NodeRunHistoryEntry): DestinationWriteResultPayload | null {
    if (entry.contract !== 'DestinationWriteResult') return null;

    const json = this.payloadCache().get(entry.workflowNodeRunId)?.deliveryDetailJson;
    if (!json) return null;

    try {
      // Returned whenever it parses. This used to require a DownloadUrl or an EmailDelivery, which meant
      // every destination that has neither — SQL Server, Postgres, any plain database writer — had its
      // result silently discarded and the node rendered "completed with no recorded output", even though
      // the run had written hundreds of records and the payload was sitting right here. DownloadUrl and
      // EmailDelivery are extras that only file- and email-shaped destinations produce; they were never the
      // thing that makes a write result worth showing.
      return JSON.parse(json) as DestinationWriteResultPayload;
    } catch {
      return null;
    }
  }

  /** The destination write result as formatted JSON, for the destinations whose result is just the write
   *  itself (no file to download, no email to report). Kept verbatim rather than reshaped into a field list:
   *  it is a small, stable payload and showing it as-is matches how every other node's output is presented. */
  /** The summary lines for one node — what it did, in its own terms. A mapping node reports the field
   *  mappings and transformation rules it applied; nodes that record no field-level work (a source fetch, a
   *  whole-resource normalization) return none and keep their per-resource-type counts, which for them is
   *  the honest summary rather than a placeholder. */
  nodeSummaryLinesFor(entry: NodeRunHistoryEntry): { label: string; value: string }[] {
    // The transform node's summary describes the run's transformation work as a whole, which the mapping
    // node's lineage is what actually recorded — the two nodes are the same pipeline stage split in two, and
    // the figures belong above the configured-rule list the transform node shows.
    const showsConfiguredRules = this.isTransformNode(entry) || this.isDeIdentificationNode(entry);
    const breakdown = showsConfiguredRules
      ? [...this.nodeBreakdowns().values()][0]
      : this.nodeBreakdowns().get(entry.workflowNodeId);
    if (!breakdown || breakdown.totalApplications === 0) return [];
    // Mapping nodes list their per-resource-type mapping counts and nothing else: the rule figures belong to
    // the transform and de-identification nodes, and repeating them here would state the same thing twice on
    // adjacent rows.
    if (!showsConfiguredRules) return [];

    const lines: { label: string; value: string }[] = [];

    // Everything that is not a plain field copy is a transformation rule — listed individually below, so the
    // total is what belongs on the summary line. The mapping total is deliberately NOT here: it is broken
    // out per resource type instead (see nodeResourceTypeCountsFor), which is the more useful cut and avoids
    // stating the same number twice.
    const ruleApplications = breakdown.rules
      .filter(r => r.nodeType !== 'DirectMapping')
      .reduce((sum, r) => sum + r.applications, 0);
    if (ruleApplications > 0) {
      lines.push({ label: 'Transformation rules applied', value: ruleApplications.toLocaleString() });
    }

    lines.push({ label: 'Fields written', value: breakdown.distinctFields.toLocaleString() });
    lines.push({ label: 'Resources processed', value: breakdown.distinctResources.toLocaleString() });

    const failures = breakdown.rules.reduce((sum, r) => sum + r.failedApplications, 0);
    if (failures > 0) {
      lines.push({ label: 'Failed applications', value: failures.toLocaleString() });
    }
    return lines;
  }

  /** True for the de-identification node, which shows the same per-resource-type rule breakdown as the
   *  transform node, sourced from the de-identification profile instead of the workflow's own rules. */
  isDeIdentificationNode(entry: NodeRunHistoryEntry): boolean {
    return entry.nodeType === 'DeIdentificationNode';
  }

  /** True for the node that normalizes resources — the one that shows the workflow's CONFIGURED
   *  transformation rules rather than any runtime count of its own. */
  isTransformNode(entry: NodeRunHistoryEntry): boolean {
    return entry.nodeType === 'FhirResourceTransformNode';
  }

  /** The configured rules to show under the transform node, per resource type. Empty for every other node:
   *  this is the workflow's transformation configuration, and repeating it on each node would say the same
   *  thing four times. */
  configuredRulesFor(entry: NodeRunHistoryEntry): ConfiguredResourceTypeRules[] {
    if (this.isDeIdentificationNode(entry)) return this.configuredDeIdRules();
    return this.isTransformNode(entry) ? this.configuredRules() : [];
  }

  /** Each resource type this node touched, with how many mappings it applied to that type — the answer to
   *  "how many of those mappings were Observations?", which a single run-wide total could never give.
   *  Replaces the plain per-resource-type item counts for nodes that record field lineage. */
  nodeResourceTypeCountsFor(entry: NodeRunHistoryEntry): LineageResourceTypeCount[] {
    return this.nodeBreakdowns().get(entry.workflowNodeId)?.resourceTypes ?? [];
  }

  /** The individual transformation rules a node applied, most-used first — the detail behind the
   *  "Transformation rules applied" summary line. Plain field copies are excluded: they are counted
   *  separately as mappings and would otherwise swamp the list. */
  nodeRuleBreakdownFor(entry: NodeRunHistoryEntry): LineageRuleCount[] {
    const breakdown = this.nodeBreakdowns().get(entry.workflowNodeId);
    if (!breakdown) return [];
    return breakdown.rules.filter(r => r.nodeType !== 'DirectMapping');
  }

  /** The count shown in a node's collapsed header. Destination nodes carry no ItemCount — the number that
   *  matters for them is RecordsWritten, which lives inside the write result — so without this they were the
   *  only node in the run with no count at all, despite having written every record the run produced. */
  headerItemCountFor(entry: NodeRunHistoryEntry): number | null {
    if (entry.itemCount !== null) return entry.itemCount;
    return this.destinationWriteResultFor(entry)?.RecordsWritten ?? null;
  }

  /** The destination write result as labelled figures, for destinations whose result is just the write itself
   *  (no file to download, no email to report). A field list rather than raw JSON so it reads in the same
   *  typeface and layout as every other node in the run — a monospaced payload block made the last node look
   *  like it belonged to a different screen. Null fields are omitted: "DownloadUrl: null" is noise for a
   *  database destination that could never have produced one. */
  destinationWriteLinesFor(entry: NodeRunHistoryEntry): { label: string; value: string }[] {
    const result = this.destinationWriteResultFor(entry);
    if (!result || result.DownloadUrl || result.EmailDelivery) return [];

    const lines: { label: string; value: string }[] = [];
    lines.push({ label: 'Records written', value: result.RecordsWritten.toLocaleString() });
    if (result.WrittenAt) {
      lines.push({ label: 'Written at', value: new Date(result.WrittenAt).toLocaleString() });
    }
    if (result.DestinationId) {
      lines.push({ label: 'Destination', value: result.DestinationId });
    }
    return lines;
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
      // validate-run outcomes. Validated is non-terminal ("checked, waiting to run"); Expired is what the
      // sweep turns an abandoned one into. Spelled out rather than left to the ?? fallback, which would show
      // raw PascalCase enum names to the user.
      Validated: 'Validated',
      ValidationFailed: 'Validation Failed',
      Expired: 'Expired',
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

  /** Copies whatever's actually shown in the expanded panel for this node — the per-resource-type counts on
   *  success, or the error message when it failed/was cancelled — so there's always something sensible to copy
   *  regardless of outcome. Counts only: node output is not retained. */
  /** Copies whatever the expanded row is actually showing. Destination nodes have no resource counts —
   *  their detail IS the write result — so this used to find nothing to copy and return silently, leaving
   *  the button looking broken. Ordered to match what the row renders: counts, else write result, else error. */
  copyNodeDetail(entry: NodeRunHistoryEntry): void {
    const counts = this.resourceCountsFor(entry.workflowNodeRunId);
    const text = counts.length
      ? counts.map(([resourceType, count]) => `${resourceType}: ${count}`).join('\n')
      : (this.destinationWriteLinesFor(entry).map(line => line.label + ': ' + line.value).join('\n')
          || entry.errorMessage || '');
    if (!text) { return; }

    navigator.clipboard.writeText(text).then(
      () => this.toast.show('Copied', `${this.nodeLabel(entry)} details copied to clipboard.`),
      () => this.toast.show('Copy failed', 'Select the text manually.'),
    );
  }
}
