import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import {
  WorkflowApiService,
  WorkflowSummary,
  DestinationData,
} from '../../services/workflow-api.service';
import { ToastService } from '../../services/toast.service';

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

interface CopyModal {
  workflowId: string;
  sourceName: string;
  name: string;
  busy: boolean;
}

type SortColumn = 'name' | 'source' | 'audience' | 'status' | 'lastRun';
type SortDirection = 'asc' | 'desc';

@Component({
  selector: 'app-workflow-list',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './workflow-list.component.html',
  styleUrl: './workflow-list.component.scss',
})
export class WorkflowListComponent implements OnInit {
  private readonly api = inject(WorkflowApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly summaries = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly searchQuery = signal('');

  /** Sort state — clicking the same column again flips direction, a new column starts ascending. */
  readonly sortColumn = signal<SortColumn>('name');
  readonly sortDirection = signal<SortDirection>('asc');

  readonly pageSizeOptions = [10, 20, 50] as const;
  readonly pageSize = signal<number>(this.pageSizeOptions[0]);
  readonly pageIndex = signal(0);

  /** Workflow id currently running/launching — disables its action button. */
  readonly busyId = signal<string | null>(null);
  /** Workflow id whose enable/disable or delete call is in flight — disables its row controls. */
  readonly rowBusyId = signal<string | null>(null);

  readonly launchModal = signal<LaunchModal | null>(null);
  readonly dataModal = signal<DataModal | null>(null);
  /** Workflow pending delete confirmation. */
  readonly confirmDelete = signal<WorkflowSummary | null>(null);
  /** Workflow pending a copy — the modal's name field always starts empty. */
  readonly copyModal = signal<CopyModal | null>(null);

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.summaries();
    return this.summaries().filter(
      w =>
        w.name.toLowerCase().includes(q) ||
        (w.applicationType?.toLowerCase().includes(q) ?? false),
    );
  });

  readonly sorted = computed(() => {
    const col = this.sortColumn();
    const dir = this.sortDirection() === 'asc' ? 1 : -1;
    const key = (w: WorkflowSummary): string | number => {
      switch (col) {
        case 'name':     return w.name.toLowerCase();
        case 'source':   return (w.sourceSystemType ?? '').toLowerCase();
        case 'audience': return this.audienceLabel(w.applicationType).toLowerCase();
        case 'status':   return w.status;
        case 'lastRun':  return w.lastRunAt ? new Date(w.lastRunAt).getTime() : -1;
      }
    };
    return [...this.filtered()].sort((a, b) => {
      const ka = key(a);
      const kb = key(b);
      if (ka < kb) return -1 * dir;
      if (ka > kb) return 1 * dir;
      return 0;
    });
  });

  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.sorted().length / this.pageSize())));

  readonly paged = computed(() => {
    // Clamp defensively — a delete/copy can shrink the list out from under a page index
    // that pointed at the last page.
    const clampedIndex = Math.min(this.pageIndex(), this.totalPages() - 1);
    const start = clampedIndex * this.pageSize();
    return this.sorted().slice(start, start + this.pageSize());
  });

  /** First/last row numbers shown for the current page, for "Showing X–Y of Z". */
  readonly rangeStart = computed(() => {
    if (this.sorted().length === 0) return 0;
    const clampedIndex = Math.min(this.pageIndex(), this.totalPages() - 1);
    return clampedIndex * this.pageSize() + 1;
  });
  readonly rangeEnd = computed(() => Math.min(this.sorted().length, this.rangeStart() + this.pageSize() - 1));

  ngOnInit(): void {
    this.reload();
  }

  onSort(column: SortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
  }

  onPageSizeChange(size: number): void {
    this.pageSize.set(size);
    this.pageIndex.set(0);
  }

  goToPage(index: number): void {
    this.pageIndex.set(Math.min(Math.max(0, index), this.totalPages() - 1));
  }

  prevPage(): void {
    this.goToPage(this.pageIndex() - 1);
  }

  nextPage(): void {
    this.goToPage(this.pageIndex() + 1);
  }

  reload(): void {
    this.loading.set(true);
    this.api.summary().subscribe({
      next: rows => {
        this.summaries.set(rows);
        this.loading.set(false);
      },
      error: err => {
        this.loading.set(false);
        this.toast.error('Failed to load workflows.', this.messageOf(err, ''));
      },
    });
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
    this.pageIndex.set(0);
  }

  /** Opens the Pipeline Builder on a blank canvas — Workflows is now the single entry point for both list and create. */
  onNewWorkflow(): void {
    this.router.navigate(['/workflow-builder']);
  }

  onAction(row: WorkflowSummary): void {
    if (this.busyId()) return;
    this.busyId.set(row.workflowId);

    if (row.action === 'Launch') {
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
          this.toast.error('Launch URL', this.messageOf(err, 'Could not generate a launch URL.'));
        },
      });
      return;
    }

    // Backend / non-interactive: trigger a run.
    this.api.run(row.workflowId).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success('Workflow run', `"${row.name}" was triggered.`);
        this.reload();
      },
      error: err => {
        this.busyId.set(null);
        this.toast.error('Workflow run', this.messageOf(err, 'The run could not be started.'));
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
        this.toast.error('Update failed', this.messageOf(err, 'Could not change the workflow state.'));
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
        this.toast.error('Update failed', this.messageOf(err, 'Could not change the public-launch state.'));
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
        this.summaries.update(rows => rows.filter(w => w.workflowId !== row.workflowId));
        this.toast.success('Deleted', `"${row.name}" was deleted.`);
      },
      error: err => {
        this.rowBusyId.set(null);
        this.confirmDelete.set(null);
        this.toast.error('Delete failed', this.messageOf(err, 'Could not delete the workflow.'));
      },
    });
  }

  /** Opens the copy modal with an empty name field — the workflow isn't duplicated until a name is confirmed. */
  askCopy(row: WorkflowSummary): void {
    this.copyModal.set({ workflowId: row.workflowId, sourceName: row.name, name: '', busy: false });
  }

  onCopyNameInput(value: string): void {
    this.copyModal.update(m => (m ? { ...m, name: value } : m));
  }

  cancelCopy(): void {
    if (this.copyModal()?.busy) return;
    this.copyModal.set(null);
  }

  /** Confirms the copy — blocked while the name is empty/whitespace-only or a copy call is already in flight. */
  confirmCopy(): void {
    const m = this.copyModal();
    const name = m?.name.trim();
    if (!m || !name || m.busy) return;

    this.copyModal.set({ ...m, busy: true });
    this.api.copy(m.workflowId, name).subscribe({
      next: () => {
        this.copyModal.set(null);
        this.toast.success('Workflow copied', `"${name}" was created from "${m.sourceName}".`);
        this.reload();
      },
      error: err => {
        this.copyModal.update(current => (current ? { ...current, busy: false } : current));
        this.toast.error('Copy failed', this.messageOf(err, 'Could not copy the workflow.'));
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

  copy(text: string): void {
    navigator.clipboard?.writeText(text).then(
      () => this.toast.success('Copied', 'Launch URL copied to clipboard.'),
      () => this.toast.error('Copy failed', 'Could not copy to the clipboard.'),
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
}
