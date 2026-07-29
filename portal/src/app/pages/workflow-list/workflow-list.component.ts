import { Component, OnInit, OnDestroy, HostListener, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import {
  WorkflowApiService,
  WorkflowSummary,
  WorkflowRunStatus,
  DestinationData,
} from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';

/** Polling cadence for an async run's status while this screen stays open (see pollRunStatus). */
const RUN_STATUS_POLL_MS = 3000;
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

type SortColumn = 'name' | 'source' | 'audience' | 'status' | 'lastRun';
type SortDirection = 'asc' | 'desc';
/** Multi-select filter categories shown in the filter bar — see filterDefs/signalFor. */
type FilterCategory = 'status' | 'audience' | 'source';

@Component({
  selector: 'app-workflow-list',
  standalone: true,
  imports: [CommonModule, DatePipe, MatIconModule],
  templateUrl: './workflow-list.component.html',
  styleUrl: './workflow-list.component.scss',
})
export class WorkflowListComponent implements OnInit, OnDestroy {
  private readonly api = inject(WorkflowApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly summaries = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly searchQuery = signal('');

  /** Workflow id currently running/launching — disables its action button. Not set for an async ("background") run,
   *  which returns immediately so the row stays interactive; see runAsync. */
  readonly busyId = signal<string | null>(null);
  /** Workflow id whose enable/disable or delete call is in flight — disables its row controls. */
  readonly rowBusyId = signal<string | null>(null);
  /** Workflow id whose "Run (sync) / Run in background" menu is open — see toggleRunMenu. */
  readonly runMenuOpenId = signal<string | null>(null);

  /** Active status-poll intervals for in-flight async runs, keyed by workflow id — cleared on completion or
   *  when this screen is torn down (navigating away does NOT stop the run itself, only this UI's polling). */
  private readonly pollHandles = new Map<string, ReturnType<typeof setInterval>>();

  readonly launchModal = signal<LaunchModal | null>(null);
  readonly dataModal = signal<DataModal | null>(null);
  /** Workflow pending delete confirmation. */
  readonly confirmDelete = signal<WorkflowSummary | null>(null);
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

  // Centralized "click outside closes it" check — reads the click's actual target instead of relying
  // on stopPropagation() scattered across the template. A click still inside any .filter-dropdown
  // (its trigger button or, since the open panel is its DOM descendant, its checkbox panel) is left
  // alone. Called from the single onDocumentClick listener below, not its own @HostListener — see
  // that method for why.
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
    this.pollHandles.forEach(handle => clearInterval(handle));
    this.pollHandles.clear();
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
    this.router.navigate(['/workflow-builder']);
  }

  /** Default click on the primary button: sync for Launch (unaffected) and, for backwards compatibility, sync for
   *  Run too — the button waits here and shows a spinner, same as before this screen offered a choice. Pass
   *  mode: 'async' (from the split-button's dropdown, see toggleRunMenu) to dispatch a background run instead. */
  onAction(row: WorkflowSummary, mode: 'sync' | 'async' = 'sync'): void {
    this.runMenuOpenId.set(null);
    if (this.busyId()) return;

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

  /** Shows/hides the "Run (wait here) / Run in background" menu for one row. Ignored for Launch rows — the launch
   *  action never executes the pipeline itself, so there's nothing to choose sync/async for (see analysis). */
  toggleRunMenu(row: WorkflowSummary, event: Event): void {
    event.stopPropagation();
    if (row.action !== 'Run') return;
    this.runMenuOpenId.set(this.runMenuOpenId() === row.workflowId ? null : row.workflowId);
  }

  // Single document:click listener for the whole component — see closeFilterMenuIfOutside above for
  // why this must not be split across multiple @HostListener('document:click') decorators.
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    this.closeFilterMenuIfOutside(event);
    this.runMenuOpenId.set(null);
  }

  /** Dispatches the run to a background task and returns immediately (202 Accepted) — the row stays interactive
   *  and the run survives navigating to another screen; poll the returned run id for completion. */
  private runAsync(row: WorkflowSummary): void {
    this.api.run(row.workflowId, true).subscribe({
      next: result => {
        const runId = (result as WorkflowRunStatus).workflowRunId;
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
        this.pollRunStatus(row, runId);
      },
      error: err => {
        this.toast.error('Workflow run', this.messageOf(err, 'The background run could not be started.'));
      },
    });
  }

  /** Polls GET /workflow-runs/{runId}/status until it leaves "Running", then reloads the row from /summary so
   *  Last Run reflects the persisted terminal result. Stops (without affecting the server-side run) if this
   *  component is destroyed first — see ngOnDestroy. */
  private pollRunStatus(row: WorkflowSummary, runId: string): void {
    const existing = this.pollHandles.get(row.workflowId);
    if (existing) {
      clearInterval(existing);
    }

    const handle = setInterval(() => {
      this.api.runStatus(runId).subscribe({
        next: status => {
          if (status.status === 'Running') return;

          clearInterval(handle);
          this.pollHandles.delete(row.workflowId);
          const failed = status.status === 'Failed';
          this.toast[failed ? 'error' : 'success'](
            'Workflow run',
            `"${row.name}" ${failed ? 'failed' : 'completed'}.`,
          );
          this.reload();
        },
        error: () => {
          clearInterval(handle);
          this.pollHandles.delete(row.workflowId);
        },
      });
    }, RUN_STATUS_POLL_MS);

    this.pollHandles.set(row.workflowId, handle);
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
    if (this.rowBusyId()) return;
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
