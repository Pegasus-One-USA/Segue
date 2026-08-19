import { Component, OnInit, OnDestroy, HostListener, DestroyRef, inject, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import {
  WorkflowApiService,
  WorkflowSummary,
  WorkflowRunStatus,
  DestinationData,
} from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';
import { RunStatusHubService } from '../../services/run-status-hub.service';
import { PermissionService } from '../../auth/services/permission.service';

/** Debounce before a search-box keystroke triggers a server round-trip (see onSearch). */
const SEARCH_DEBOUNCE_MS = 300;

interface LaunchModal {
  name: string;
  url: string;
  opensDirectly: boolean;
  mode: 'ehr-launch' | 'standalone' | 'patient';
}

interface DataModal {
  workflowId: string;
  name: string;
  loading: boolean;
  data: DestinationData | null;
  error: string | null;
}

type SortColumn = 'name' | 'source' | 'audience' | 'status' | 'lastRun' | 'actionOn';
type SortDirection = 'asc' | 'desc';
/** Multi-select filter categories shown in the filter bar — see filterDefs/signalFor. */
type FilterCategory = 'status' | 'audience' | 'source';

@Component({
  selector: 'app-workflow-list',
  standalone: true,
  imports: [CommonModule, DatePipe, FormsModule, MatIconModule],
  templateUrl: './workflow-list.component.html',
  styleUrl: './workflow-list.component.scss',
})
export class WorkflowListComponent implements OnInit, OnDestroy {
  private readonly api = inject(WorkflowApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly runStatusHub = inject(RunStatusHubService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly permissions = inject(PermissionService);

  // ── RBAC: workflow.view (the route guard already reached here) only grants VIEW access — these four
  // are what actually gate each action-specific button/menu-item below, kept independent of one another
  // and of the source/destination-type permissions (which are unrelated — see sources/transforms filters).
  protected readonly canCreate = computed(() => this.permissions.hasPermission('workflow.create'));
  protected readonly canEdit   = computed(() => this.permissions.hasPermission('workflow.edit'));
  protected readonly canDelete = computed(() => this.permissions.hasPermission('workflow.delete'));
  protected readonly canRun    = computed(() => this.permissions.hasPermission('workflow.run'));

  readonly summaries = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly searchQuery = signal('');

  /** Workflow id currently running/launching — disables its action button. Not set for an async ("background") run,
   *  which returns immediately so the row stays interactive; see runAsync. */
  readonly busyId = signal<string | null>(null);
  /** Workflow id whose enable/disable or delete call is in flight — disables its row controls. */
  readonly rowBusyId = signal<string | null>(null);

  readonly launchModal = signal<LaunchModal | null>(null);
  readonly dataModal = signal<DataModal | null>(null);
  /** Workflow pending delete confirmation. */
  readonly confirmDelete = signal<WorkflowSummary | null>(null);
  /** Workflow pending copy (duplicate) confirmation — holds the name typed into the modal. */
  readonly confirmCopy = signal<WorkflowSummary | null>(null);
  readonly copyName = signal('');
  // Default sort surfaces the most recently run (created/updated activity proxy) workflows first —
  // per user direction, so a newly built or just-triggered workflow is immediately visible without
  // having to search/sort manually. Backend orders never-run workflows last (LastRunAt ?? -1).
  readonly sortColumn = signal<SortColumn>('lastRun');
  readonly sortDirection = signal<SortDirection>('desc');
  readonly pageSizeOptions = [10, 20, 50];
  readonly pageSize = signal(10);
  readonly pageIndex = signal(0);
  /** Total rows matching the current search, across every page — from the server, not summaries().length. */
  readonly totalCount = signal(0);

  /** The current page's rows, as returned by the server — kept as an alias so the template's existing
   *  `paged()` calls don't need to change even though pagination moved server-side. */
  readonly paged = computed(() => this.summaries());

  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.pageSize())));

  /** First/last row numbers shown for the current page, for "Showing X–Y of Z". */
  readonly rangeStart = computed(() => (this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1));
  readonly rangeEnd = computed(() => Math.min(this.totalCount(), this.rangeStart() + this.pageSize() - 1));

  private searchDebounceHandle: ReturnType<typeof setTimeout> | undefined;

  // ── Status/Audience/Source multi-select filters ─────────────────────────────
  /** Distinct option lists for each filter category, from the server's last response — the full set across every
   *  workflow (not just what's currently matching), so unchecking every box in one category doesn't make another
   *  category's checkboxes disappear. */
  readonly availableStatuses = signal<string[]>([]);
  readonly availableAudiences = signal<string[]>([]);
  readonly availableSources = signal<string[]>([]);

  readonly selectedStatuses = signal<Set<string>>(new Set());
  readonly selectedAudiences = signal<Set<string>>(new Set());
  readonly selectedSources = signal<Set<string>>(new Set());

  /** Which filter dropdown panel is open, if any — see toggleFilterMenu/closeFilterMenus. */
  readonly openFilterMenu = signal<FilterCategory | null>(null);

  /** Which row's "more actions" menu is open, if any — keyed by workflowId. See toggleActionMenu. */
  readonly openActionMenuId = signal<string | null>(null);

  /** Viewport-relative coordinates for the open action menu, computed from the trigger button's own
   *  bounding rect at open time. The panel renders position: fixed at these coordinates instead of
   *  position: absolute within the row — .table-wrapper's overflow-x: auto forces its overflow-y to
   *  auto too (a standard CSS coercion: an ancestor can't have overflow-x auto/scroll and overflow-y
   *  visible at the same time), which would otherwise clip the dropdown to almost nothing whenever it
   *  needs to extend below the table's own visible bounds.
   *  Exactly one of top/bottom is set, never both — see toggleActionMenu's flip-upward check for a
   *  trigger too close to the bottom of the viewport to fit the panel below it. `left` (not `right`) is
   *  anchored to the trigger's own left edge — the trigger now lives in the first column, so the panel
   *  opens rightward from it; anchoring by `right` (the old last-column trigger's natural edge) would
   *  push a first-column panel off the left of the viewport. */
  readonly actionMenuPosition = signal<{ top?: number; bottom?: number; left: number } | null>(null);

  /** Rough panel height (6 items + divider + padding, see .row-menu-item/.row-menu-divider) — just needs
   *  to be in the right ballpark to decide whether the panel fits below the trigger, not pixel-exact. */
  private static readonly ACTION_MENU_ESTIMATED_HEIGHT = 260;

  readonly filterDefs: { category: FilterCategory; label: string }[] = [
    { category: 'status', label: 'Status' },
    { category: 'audience', label: 'Audience' },
    { category: 'source', label: 'Source' },
  ];

  constructor() {
    // A RunStatusChangedEvent carries workflowDefinitionId + status, both of which map directly onto an
    // already-loaded row's own fields (workflowId/lastRun/lastRunAt) — unlike the Dashboard's recent-runs list,
    // this genuinely is a surgical single-row patch, not a refetch: the row already exists on screen, only its
    // status is stale. reconnected$ below reconciles anything missed while disconnected with a fresh reload().
    this.runStatusHub.ensureConnected();
    this.runStatusHub.runStatusChanged$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(event => {
        this.summaries.update(rows =>
          rows.map(w =>
            w.workflowId === event.workflowDefinitionId
              ? { ...w, lastRun: event.status, lastRunAt: event.occurredAt }
              : w,
          ),
        );
      });

    this.runStatusHub.reconnected$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.reload());
  }

  ngOnInit(): void {
    this.reload();
  }

  optionsFor(category: FilterCategory): string[] {
    switch (category) {
      case 'status':   return this.availableStatuses();
      case 'audience': return this.availableAudiences();
      case 'source':   return this.availableSources();
    }
  }

  /** Audience options are the raw ApplicationType enum names (EhrLaunch/Standalone/...) — display the same
   *  friendly label the Audience column already uses; every other category displays its raw value as-is. */
  displayLabelFor(category: FilterCategory, value: string): string {
    return category === 'audience' ? this.audienceLabel(value) : value;
  }

  selectedSetFor(category: FilterCategory): Set<string> {
    switch (category) {
      case 'status':   return this.selectedStatuses();
      case 'audience': return this.selectedAudiences();
      case 'source':   return this.selectedSources();
    }
  }

  private signalForCategory(category: FilterCategory) {
    switch (category) {
      case 'status':   return this.selectedStatuses;
      case 'audience': return this.selectedAudiences;
      case 'source':   return this.selectedSources;
    }
  }

  selectedCountFor(category: FilterCategory): number {
    return this.selectedSetFor(category).size;
  }

  isFilterSelected(category: FilterCategory, value: string): boolean {
    return this.selectedSetFor(category).has(value);
  }

  toggleFilterMenu(category: FilterCategory): void {
    this.openFilterMenu.set(this.openFilterMenu() === category ? null : category);
  }

  toggleActionMenu(event: MouseEvent, workflowId: string): void {
    if (this.openActionMenuId() === workflowId) {
      this.openActionMenuId.set(null);
      this.actionMenuPosition.set(null);
      return;
    }

    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const left = rect.left;
    const spaceBelow = window.innerHeight - rect.bottom;

    this.actionMenuPosition.set(
      spaceBelow < WorkflowListComponent.ACTION_MENU_ESTIMATED_HEIGHT
        ? { bottom: window.innerHeight - rect.top + 6, left }
        : { top: rect.bottom + 6, left }
    );
    this.openActionMenuId.set(workflowId);
  }

  // Centralized "click outside closes it" check — reads the click's actual target instead of relying
  // on stopPropagation() scattered across the template. A click still inside any .filter-dropdown
  // (its trigger button or, since the open panel is its DOM descendant, its checkbox panel) is left
  // alone. Called from the single onDocumentClick listener below, not its own @HostListener — see
  // that method for why.
  private closeFilterMenuIfOutside(event: MouseEvent): void {
    if (!(event.target as HTMLElement).closest('.filter-dropdown')) {
      this.openFilterMenu.set(null);
    }
    if (!(event.target as HTMLElement).closest('.row-menu')) {
      this.openActionMenuId.set(null);
      this.actionMenuPosition.set(null);
    }
  }

  toggleFilterValue(category: FilterCategory, value: string): void {
    this.signalForCategory(category).update(current => {
      const next = new Set(current);
      if (next.has(value)) next.delete(value); else next.add(value);
      return next;
    });
    this.pageIndex.set(0);
    this.reload();
  }

  clearFilter(category: FilterCategory): void {
    this.signalForCategory(category).set(new Set());
    this.pageIndex.set(0);
    this.reload();
  }

  reset(): void {
    this.searchQuery.set('');
    this.selectedStatuses.set(new Set());
    this.selectedAudiences.set(new Set());
    this.selectedSources.set(new Set());
    this.sortColumn.set('lastRun');
    this.sortDirection.set('desc');
    this.pageIndex.set(0);
    this.reload();
  }

  onSort(column: SortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.pageIndex.set(0);
    this.reload();
  }

  onPageSizeChange(size: number): void {
    this.pageSize.set(size);
    this.pageIndex.set(0);
    this.reload();
  }

  prevPage(): void {
    if (this.pageIndex() === 0) return;
    this.pageIndex.update(i => i - 1);
    this.reload();
  }

  nextPage(): void {
    if (this.pageIndex() >= this.totalPages() - 1) return;
    this.pageIndex.update(i => i + 1);
    this.reload();
  }

  ngOnDestroy(): void {
    if (this.searchDebounceHandle) {
      clearTimeout(this.searchDebounceHandle);
    }
  }

  reload(): void {
    this.loading.set(true);
    this.api
      .summary({
        page: this.pageIndex() + 1,
        pageSize: this.pageSize(),
        search: this.searchQuery().trim() || undefined,
        sortColumn: this.sortColumn(),
        sortDirection: this.sortDirection(),
        statuses: [...this.selectedStatuses()],
        applicationTypes: [...this.selectedAudiences()],
        sourceSystemTypes: [...this.selectedSources()],
      })
      .subscribe({
        next: result => {
          this.summaries.set(result.items);
          this.totalCount.set(result.totalCount);
          this.availableStatuses.set(result.availableStatuses);
          this.availableAudiences.set(result.availableApplicationTypes);
          this.availableSources.set(result.availableSourceSystemTypes);
          this.loading.set(false);
          // A delete/copy (or a filter/page-size change) can shrink the matching set out from under a page index
          // that pointed past the new last page — snap back and refetch rather than showing an empty page.
          const lastPageIndex = Math.max(0, Math.ceil(result.totalCount / this.pageSize()) - 1);
          if (this.pageIndex() > lastPageIndex) {
            this.pageIndex.set(lastPageIndex);
            this.reload();
          }
        },
        error: err => {
          this.loading.set(false);
          this.toast.error(this.messageOf(err, 'Failed to load workflows.'));
        },
      });
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
    this.pageIndex.set(0);
    if (this.searchDebounceHandle) {
      clearTimeout(this.searchDebounceHandle);
    }
    this.searchDebounceHandle = setTimeout(() => this.reload(), SEARCH_DEBOUNCE_MS);
  }

  /** Opens the Pipeline Builder on a blank canvas — Workflows is now the single entry point for both list and create. */
  onNewWorkflow(): void {
    if (!this.canCreate()) return;
    this.router.navigate(['/workflow-builder']);
  }

  /** Launch rows are unaffected (still their own thing — see below). Every Run row now always dispatches in the
   *  background (the template only ever passes mode: 'async' — see the row-actions template) so the row stays
   *  interactive and the run survives navigating away; live status arrives via SignalR (RunStatusHubService)
   *  instead of the old blocking "wait here" option, which is no longer offered in the UI. The 'sync' branch
   *  below is kept as-is (still the API's supported synchronous mode) rather than deleted outright — it's simply
   *  unreached from this screen now that mode always defaults from an explicit 'async' call. */
  onAction(row: WorkflowSummary, mode: 'sync' | 'async' = 'sync'): void {
    if (this.busyId() || !this.canRun()) return;

    if (row.action === 'Launch') {
      this.busyId.set(row.workflowId);
      this.api.launchUrl(row.workflowId).subscribe({
        next: result => {
          this.busyId.set(null);
          this.launchModal.set({
            name: row.name,
            url: result.launchUrl,
            opensDirectly: result.opensDirectly ?? false,
            mode: result.mode ?? 'ehr-launch',
          });
        },
        error: err => {
          this.busyId.set(null);
          this.toast.error(this.messageOf(err, 'Could not generate a launch URL.'));
        },
      });
      return;
    }

    if (mode === 'async') {
      this.runAsync(row);
      return;
    }

    // Backend / non-interactive, synchronous: blocks here until the whole run finishes (or the request is
    // aborted by navigating away) — same behavior as before "Run in background" existed.
    this.busyId.set(row.workflowId);
    this.api.run(row.workflowId).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success('Workflow run', `"${row.name}" was triggered.`);
        this.reload();
      },
      error: err => {
        this.busyId.set(null);
        this.toast.error(this.runFailureMessage(err));
      },
    });
  }

  // Single document:click listener for the whole component — see closeFilterMenuIfOutside above for
  // why this must not be split across multiple @HostListener('document:click') decorators.
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    this.closeFilterMenuIfOutside(event);
  }

  /** Dispatches the run to a background task and returns immediately (202 Accepted) — the row stays interactive
   *  and the run survives navigating to another screen. Completion arrives via the SignalR push
   *  (RunStatusHubService.runStatusChanged$, wired in the constructor above) rather than polling here. */
  private runAsync(row: WorkflowSummary): void {
    this.api.run(row.workflowId, true).subscribe({
      next: result => {
        const correlationId = (result as WorkflowRunStatus).correlationId;
        this.toast.success(
          'Running in background',
          `"${row.name}" is running — you can navigate away, it keeps running on the server.`
            + (correlationId ? ` Correlation ID: ${correlationId}` : ''),
        );
        this.summaries.update(rows =>
          rows.map(w =>
            w.workflowId === row.workflowId
              ? { ...w, lastRun: 'Running', lastRunAt: new Date().toISOString() }
              : w,
          ),
        );
      },
      error: err => {
        this.toast.error('Workflow run', this.messageOf(err, 'The background run could not be started.'));
      },
    });
  }

  onViewData(row: WorkflowSummary): void {
    this.dataModal.set({
      workflowId: row.workflowId,
      name: row.name,
      loading: true,
      data: null,
      error: null,
    });

    this.api.destinationData(row.workflowId).subscribe({
      next: data =>
        this.dataModal.update(m => (m ? { ...m, loading: false, data, error: data.error } : m)),
      error: err =>
        this.dataModal.update(m =>
          m
            ? { ...m, loading: false, error: this.messageOf(err, 'Could not read destination data.') }
            : m,
        ),
    });
  }

  /** Open the workflow in the Pipeline Builder for full graph editing (Save there issues a PUT update). */
  onEdit(row: WorkflowSummary): void {
    this.router.navigate(['/workflow-builder'], { queryParams: { id: row.workflowId } });
  }

  /** Enable/disable toggle via the activate/deactivate endpoints. */
  onToggleEnabled(row: WorkflowSummary): void {
    if (this.rowBusyId() || !this.canEdit()) return;
    this.rowBusyId.set(row.workflowId);
    const enabling = row.status === 'Disabled';
    const call = enabling ? this.api.activate(row.workflowId) : this.api.deactivate(row.workflowId);
    call.subscribe({
      next: () => {
        this.rowBusyId.set(null);
        this.summaries.update(rows =>
          rows.map(w =>
            w.workflowId === row.workflowId ? { ...w, status: enabling ? 'Enabled' : 'Disabled' } : w,
          ),
        );
        this.toast.success(enabling ? 'Enabled' : 'Disabled', `"${row.name}" is now ${enabling ? 'enabled' : 'disabled'}.`);
      },
      error: err => {
        this.rowBusyId.set(null);
        this.toast.error(this.messageOf(err, 'Could not change the workflow state.'));
      },
    });
  }

  /** Opts a workflow in/out of the anonymous public-standalone-url mint endpoint (see OAuthController.
   *  GetPublicWorkflowStandaloneUrl) — the only gate letting a third-party app's own hospital picker mint a
   *  working Epic-login link for it without a FHIRBridge admin session. */
  onTogglePublicLaunch(row: WorkflowSummary): void {
    if (this.rowBusyId()) return;
    this.rowBusyId.set(row.workflowId);
    const enabling = !row.isPubliclyLaunchable;
    const call = enabling ? this.api.enablePublicLaunch(row.workflowId) : this.api.disablePublicLaunch(row.workflowId);
    call.subscribe({
      next: () => {
        this.rowBusyId.set(null);
        this.summaries.update(rows =>
          rows.map(w =>
            w.workflowId === row.workflowId ? { ...w, isPubliclyLaunchable: enabling } : w,
          ),
        );
        this.toast.success(
          enabling ? 'Public launch enabled' : 'Public launch disabled',
          enabling
            ? `"${row.name}" can now be launched anonymously by a third-party app's hospital picker.`
            : `"${row.name}" can no longer be launched anonymously.`,
        );
      },
      error: err => {
        this.rowBusyId.set(null);
        this.toast.error(this.messageOf(err, 'Could not change the public-launch state.'));
      },
    });
  }

  askDelete(row: WorkflowSummary): void {
    if (!this.canDelete()) return;
    this.confirmDelete.set(row);
  }

  cancelDelete(): void {
    this.confirmDelete.set(null);
  }

  confirmDeleteWorkflow(): void {
    const row = this.confirmDelete();
    if (!row) return;
    this.rowBusyId.set(row.workflowId);
    this.api.delete(row.workflowId).subscribe({
      next: () => {
        this.rowBusyId.set(null);
        this.confirmDelete.set(null);
        this.reload();
        this.toast.success('Deleted', `"${row.name}" was deleted.`);
      },
      error: err => {
        this.rowBusyId.set(null);
        this.confirmDelete.set(null);
        this.toast.error(this.messageOf(err, 'Could not delete the workflow.'));
      },
    });
  }

  /** Copies this workflow's raw id straight to the clipboard — no modal, just the id + a toast confirmation. */
  copyWorkflowId(row: WorkflowSummary): void {
    this.copy(row.workflowId, 'Workflow ID');
  }

  /** Opens the duplicate-workflow modal, pre-filling a "<name> (copy)" suggestion. Duplicating produces a
   *  brand-new workflow (+ cloned source/destination rows) — create semantics, same as the backend's own
   *  POST /workflows/{id}/copy gate. */
  askCopyWorkflow(row: WorkflowSummary): void {
    if (!this.canCreate()) return;
    this.copyName.set(`${row.name} (copy)`);
    this.confirmCopy.set(row);
  }

  cancelCopyWorkflow(): void {
    this.confirmCopy.set(null);
    this.copyName.set('');
  }

  /** Duplicates the workflow under the typed name. The copy is always created disabled (see
   *  WorkflowEndpoints.copy) so it never double-runs alongside the original — use the row's
   *  Enable toggle once you've reviewed it. */
  confirmCopyWorkflow(): void {
    const row = this.confirmCopy();
    const name = this.copyName().trim();
    if (!row || !name) return;
    this.rowBusyId.set(row.workflowId);
    this.api.copy(row.workflowId, name).subscribe({
      next: () => {
        this.rowBusyId.set(null);
        this.confirmCopy.set(null);
        this.copyName.set('');
        this.reload();
        this.toast.success('Workflow copied', `"${name}" was created (disabled). Enable it when you're ready.`);
      },
      error: err => {
        this.rowBusyId.set(null);
        this.toast.error(this.messageOf(err, 'Could not copy the workflow.'));
      },
    });
  }

  /** Friendly SMART application-type label for the Audience column. */
  audienceLabel(applicationType: string | null): string {
    switch (applicationType) {
      case 'EhrLaunch':  return 'EHR Launch (Provider)';
      case 'Standalone': return 'Provider Standalone';
      case 'Patient':    return 'Patient Standalone';
      case 'Backend':    return 'Backend Service';
      default:           return '—';
    }
  }

  closeLaunchModal(): void {
    this.launchModal.set(null);
  }

  closeDataModal(): void {
    this.dataModal.set(null);
  }

  copy(text: string, label = 'Link'): void {
    navigator.clipboard?.writeText(text).then(
      () => this.toast.success('Copied', `${label} copied to clipboard.`),
      () => this.toast.error('Could not copy to the clipboard.'),
    );
  }

  runStatusClass(status: string | null): string {
    switch (status) {
      case 'Succeeded': return 'status-completed';
      case 'Running':   return 'status-running';
      case 'Failed':    return 'status-failed';
      default:          return 'status-none';
    }
  }

  private messageOf(err: unknown, fallback: string): string {
    const e = err as { error?: { title?: string; error_description?: string } | string; message?: string };
    const body = e?.error;
    if (body && typeof body === 'object') {
      return body.error_description || body.title || e?.message || fallback;
    }
    return e?.message || fallback;
  }

  /** The backend's Error Reference ID (ERR-…) when a run failed technically — quotable to support. */
  private refOf(err: unknown): string | null {
    const body = (err as { error?: { errorReferenceId?: string } })?.error;
    return body && typeof body === 'object' ? body.errorReferenceId ?? null : null;
  }

  /** A generic, PHI-safe failure message for a run — with the reference id appended when present so it can be
   *  looked up in Operations → Errors. Never surfaces the raw exception text. */
  private runFailureMessage(err: unknown): string {
    const ref = this.refOf(err);
    return ref
      ? `The run could not be started. Reference: ${ref}`
      : this.messageOf(err, 'The run could not be started.');
  }
}
