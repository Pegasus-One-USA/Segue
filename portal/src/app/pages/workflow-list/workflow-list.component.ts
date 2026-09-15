import { Component, OnInit, OnDestroy, HostListener, DestroyRef, inject, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatDividerModule } from '@angular/material/divider';
import {
  WorkflowApiService,
  WorkflowSummary,
  WorkflowLifecycleStatus,
  WorkflowRunStatus,
  DestinationData,
} from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';
import { RunStatusHubService } from '../../services/run-status-hub.service';
import { PermissionService } from '../../auth/services/permission.service';
import { sourceSystemDisplayName } from '../../data/source-system-display-names.data';

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
  imports: [CommonModule, DatePipe, FormsModule, MatButtonModule, MatIconModule, MatMenuModule, MatDividerModule, RouterLink],
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
  /** True only while a search-box-triggered reload is in flight — see onSearch/reload. Drives a small inline
   *  spinner in the search box instead of the app-wide global loader, which would otherwise blur the very
   *  input the user is still typing into (AppComponent marks the routed content `[inert]` while busy). */
  readonly searching = signal(false);

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
              ? { ...w, lastRun: event.status, lastRunAt: event.occurredAt, lastRunErrorReferenceId: event.errorReferenceId }
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
    if (category === 'audience') return this.audienceLabel(value);
    // The filter's VALUE stays the enum member the API filters on; only the text changes.
    if (category === 'source') return this.sourceSystemLabel(value);
    return value;
  }

  /** Brand name for a source system — the Source badge and the Source filter must agree. */
  sourceSystemLabel(sourceSystemType: string | null | undefined): string {
    return sourceSystemDisplayName(sourceSystemType);
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

  // Centralized "click outside closes it" check — reads the click's actual target instead of relying
  // on stopPropagation() scattered across the template. A click still inside any .filter-dropdown
  // (its trigger button or, since the open panel is its DOM descendant, its checkbox panel) is left
  // alone. Called from the single onDocumentClick listener below, not its own @HostListener — see
  // that method for why. The row action menu is now a real mat-menu (CDK overlay), which already
  // handles its own outside-click/Esc dismissal, so it no longer needs handling here.
  private closeFilterMenuIfOutside(event: MouseEvent): void {
    if (!(event.target as HTMLElement).closest('.filter-dropdown')) {
      this.openFilterMenu.set(null);
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

  /** `silent` (only ever passed from onSearch's debounce) skips the app-wide global loader for this request —
   *  see WorkflowApiService.summary for why: without it, every debounced keystroke blurs the search box mid-type. */
  reload(silent = false): void {
    this.loading.set(true);
    if (silent) this.searching.set(true);
    this.api
      .summary(
        {
          page: this.pageIndex() + 1,
          pageSize: this.pageSize(),
          search: this.searchQuery().trim() || undefined,
          sortColumn: this.sortColumn(),
          sortDirection: this.sortDirection(),
          statuses: [...this.selectedStatuses()],
          applicationTypes: [...this.selectedAudiences()],
          sourceSystemTypes: [...this.selectedSources()],
        },
        silent,
      )
      .subscribe({
        next: result => {
          this.summaries.set(result.items);
          this.totalCount.set(result.totalCount);
          this.availableStatuses.set(result.availableStatuses);
          this.availableAudiences.set(result.availableApplicationTypes);
          this.availableSources.set(result.availableSourceSystemTypes);
          this.loading.set(false);
          this.searching.set(false);
          // A delete/copy (or a filter/page-size change) can shrink the matching set out from under a page index
          // that pointed past the new last page — snap back and refetch rather than showing an empty page.
          const lastPageIndex = Math.max(0, Math.ceil(result.totalCount / this.pageSize()) - 1);
          if (this.pageIndex() > lastPageIndex) {
            this.pageIndex.set(lastPageIndex);
            this.reload(silent);
          }
        },
        error: err => {
          this.loading.set(false);
          this.searching.set(false);
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
    this.searchDebounceHandle = setTimeout(() => this.reload(true), SEARCH_DEBOUNCE_MS);
  }

  // ── New workflow (name + description first) ──────────────────────────────
  //
  // The workflow is created BEFORE the canvas opens, so it always has a real id. That is what removes the
  // "authored before the workflow existed" class of bug at its root: node config, mapping and transform rules
  // no longer have to be parked somewhere without an owner and reconciled on a later save.
  readonly showNewWorkflow = signal(false);
  readonly newWorkflowName = signal('');
  readonly newWorkflowDescription = signal('');
  readonly creatingWorkflow = signal(false);

  /** Opens the name/description modal. The canvas is only reached once the workflow is actually created. */
  onNewWorkflow(): void {
    if (!this.canCreate()) return;
    this.newWorkflowName.set('');
    this.newWorkflowDescription.set('');
    this.showNewWorkflow.set(true);
  }

  cancelNewWorkflow(): void {
    if (this.creatingWorkflow()) return;
    this.showNewWorkflow.set(false);
  }

  /** Creates an empty workflow (no nodes, no edges) and opens the builder on it. It starts life as Draft —
   *  it has no destination yet — and is created enabled, so adding a destination is all it takes to be Ready. */
  confirmNewWorkflow(): void {
    const name = this.newWorkflowName().trim();
    if (!name || this.creatingWorkflow()) return;

    const description = this.newWorkflowDescription().trim();
    this.creatingWorkflow.set(true);
    this.api
      .save({ name, description: description || null, isEnabled: true, nodes: [], edges: [] })
      .subscribe({
        next: created => {
          this.creatingWorkflow.set(false);
          this.showNewWorkflow.set(false);
          // new=1 tells the builder this workflow has an id but an EMPTY graph, so it resets the canvas
          // instead of taking the edit path — see its ngOnInit. Without it the builder treats the id as
          // an existing workflow to load, and the stale canvas state breaks the `+` picker.
          this.router.navigate(['/workflow-builder-v2'], {
            queryParams: { id: created.id, new: '1' },
          });
        },
        error: err => {
          this.creatingWorkflow.set(false);
          this.toast.error(this.messageOf(err, 'Could not create the workflow.'));
        },
      });
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

  /**
   * Open the workflow in the Pipeline Builder for full graph editing (Save there issues a PUT update).
   *
   * Now a straight navigation. It used to fetch the definition first purely to decide WHICH builder to
   * open, by looking for a V2-only chain step — a guess that could not tell a V2 workflow containing no
   * such step (a bare Source → Destination) from a V1 one, and so silently opened it in a builder that
   * could not configure Field Mapping. With V1 retired (plan §8) there is nothing to detect.
   */
  onEdit(row: WorkflowSummary): void {
    this.openBuilder(row.workflowId);
  }

  private openBuilder(workflowId: string): void {
    this.router.navigate(['/workflow-builder-v2'], { queryParams: { id: workflowId } });
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
        // Re-enabling does not simply restore "Enabled" — Draft/Ready is derived from the graph, so a
        // workflow with no destination wired up goes back to Draft, exactly as the server would report it
        // on the next reload. Getting this wrong would show a stale "Ready" until the list refreshed.
        this.summaries.update(rows =>
          rows.map(w =>
            w.workflowId === row.workflowId
              ? { ...w, status: enabling ? (w.hasDestination ? 'Ready' : 'Draft') : 'Disabled' }
              : w,
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
    if (this.rowBusyId() || !this.canEdit()) return;
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

  /** Deep-links to the Execution History screen pre-filtered to this one workflow's runs (see
   *  ExecutionHistoryListComponent's workflowIdFilter/?workflowId= handling) — same backend param the
   *  Dashboard's ?status= tile links already established the pattern for. */
  onViewExecutionHistory(row: WorkflowSummary): void {
    this.router.navigate(['/execution-history'], { queryParams: { workflowId: row.workflowId } });
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

  /** Tooltip for the lifecycle badge — "Draft" on its own does not say why the workflow cannot run. */
  statusHint(status: WorkflowLifecycleStatus): string {
    return {
      Draft: 'No destination configured yet, so this workflow cannot run. Add one to make it Ready.',
      Ready: 'Source and destination are configured — this workflow will run.',
      Disabled: 'Deliberately paused. Enable it from the row menu to make it runnable again.',
    }[status];
  }

  /** Deliberately mirrors ExecutionHistoryListComponent's statusClass so the same run never renders as two
   *  different badges across the two screens. AwaitingBulkExport is non-terminal (a node deferred to an async
   *  $export job and the run is still in flight), so it reuses the Running badge rather than the queued one;
   *  PartialSuccess is terminal and fully written, so it reuses Succeeded rather than reading as an error. */
  runStatusClass(status: string | null): string {
    const map: Record<string, string> = {
      Pending: 'badge-queued',
      Running: 'badge-running',
      AwaitingBulkExport: 'badge-running',
      Succeeded: 'badge-completed',
      PartialSuccess: 'badge-completed',
      Failed: 'badge-failed',
      Cancelled: 'badge-inactive',
    };
    return `badge ${(status && map[status]) ?? 'badge-queued'}`;
  }

  /** Same label map as ExecutionHistoryListComponent.statusLabel — without it the raw enum name leaks into the
   *  table ("AwaitingBulkExport" instead of "Awaiting Bulk Export"). */
  runStatusLabel(status: string | null): string {
    if (!status) return '';
    return (
      {
        Pending: 'Pending',
        Running: 'Running',
        AwaitingBulkExport: 'Running',
        Succeeded: 'Succeeded',
        PartialSuccess: 'Partial Success',
        Failed: 'Failed',
        Cancelled: 'Cancelled',
      }[status] ?? status
    );
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
