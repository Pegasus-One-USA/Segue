import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { DestinationConfigurationService } from '../../services/destination-configuration.service';
import { DestinationConfigurationDto, DestinationSortColumn, DestinationType, SortOrder } from '../../models/destination-configuration.model';
import {
  DestinationConnectionDialogComponent,
  DestinationConnectionDialogData,
  DESTINATION_CREATE_PERMISSION_CODES,
} from '../../dialogs/destination-connection-dialog/destination-connection-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { TRANSFORMS } from '../../../data/transforms.data';

/**
 * Standalone admin CRUD for DestinationConfiguration rows — server-side paged/filtered (no existing screen in
 * the portal does this today; every other list fetches everything and paginates client-side). Edit/Delete are
 * gated by execution history: a row with pipeline run history can only be viewed, never edited or deleted,
 * enforced here for the buttons and again server-side (the actual source of truth) on the PUT/DELETE calls.
 */
@Component({
  selector: 'app-destination-connection-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    PaginationBarComponent,
    HideWithoutPermissionDirective,
  ],
  templateUrl: './destination-connection-list.component.html',
  styleUrls: ['./destination-connection-list.component.scss'],
})
export class DestinationConnectionListComponent implements OnInit {
  private readonly svc         = inject(DestinationConfigurationService);
  private readonly dialog      = inject(MatDialog);
  private readonly toast       = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  // ─── Permission gating ──────────────────────────────────────────────────────
  // There is no `destinationconnections.create` — Create is authorized per destination type
  // (ConfigurationsController.cs). "Can this role create a destination connection at all" = holds the create
  // code for ANY type the dialog's create-flow offers. That list (and each type's permission prefix) is the
  // single source shared with the dialog, so adding a type there widens this gate automatically.
  readonly CREATE_CODES = DESTINATION_CREATE_PERMISSION_CODES;

  /** `{destinationType}.edit`, e.g. `sqlserver.edit` — the code the backend actually authorizes
   *  PUT /destinations/{id} against, independent of the generic destinationconnections group (which
   *  has no edit action of its own). */
  editCode(item: DestinationConfigurationDto): string {
    return `${item.destinationType.toLowerCase()}.edit`;
  }

  /** Delete requires BOTH the generic destinationconnections.delete AND the type-specific
   *  `{destinationType}.delete` (ConfigurationsController.cs checks both) — mirrored here with
   *  mode:'all' rather than either alone, so a role missing either one doesn't see a Delete button
   *  the backend would reject. */
  deleteCodes(item: DestinationConfigurationDto): string[] {
    return ['destinationconnections.delete', `${item.destinationType.toLowerCase()}.delete`];
  }

  /** The combined View/Edit icon button (label swaps per hasHistory) is always safe to show for a row
   *  already-visible in this view.list — a history-locked row is VIEW-only regardless of edit
   *  permission, same as workflow-list's "View in Workflow Builder" relabeling; only the editable case
   *  needs the permission check, since that's the one that actually lets a mutation through. */
  canOpenEntity(item: DestinationConfigurationDto): boolean {
    return this.hasHistory(item) || this.permissions.hasPermission(this.editCode(item));
  }

  readonly searchQuery = signal('');
  readonly typeFilter = signal<DestinationType | ''>('');
  readonly statusFilter = signal<'' | 'true' | 'false'>('');
  readonly sortColumn = signal<DestinationSortColumn>('name');
  readonly sortDirection = signal<SortOrder>('asc');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly loading = signal(true);

  readonly items = signal<DestinationConfigurationDto[]>([]);
  readonly totalCount = signal(0);
  /** id -> hasExecutionHistory, loaded alongside each page so Edit/Delete can be gated without a per-click round trip. */
  readonly historyById = signal<Record<string, boolean>>({});
  /** ids currently referenced by at least one workflow's Destination node, regardless of run history — gates
   *  Delete independently of historyById (a never-run destination can still be wired into a live workflow). */
  readonly usedInWorkflowIds = signal<Set<string>>(new Set());

  readonly displayedCols = ['name', 'destinationType', 'target', 'isEnabled', 'actionBy', 'actionOn', 'actions'];

  /** Only one sortable column today — "Action on" — server-driven since this list is server-paged. */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
    this.pageIndex.set(0);
    this.load();
  }

  private clearActionOnSort(): void {
    this.actionOnSortDirection.set(null);
  }

  /**
   * Type-filter options, derived from the same TRANSFORMS catalog the workflow builder renders its
   * Node Library destinations from — so the filter always offers exactly the destination types that
   * appear at workflow-create time (and picks up any new one added to the catalog automatically),
   * grouped by the catalog's own category and in catalog order. Each group becomes an <optgroup>.
   *
   * Gated by the identical rule the Node Library's Rank-7 tiles use (node-library-dialog.component.ts):
   * a type with a dedicated permission group is only offered when the user holds `{prefix}.view`
   * (admins hold everything). Built once — permissions are decoded before bootstrap and don't change
   * while this screen is mounted.
   */
  readonly typeGroups: { category: string; options: { value: DestinationType; label: string }[] }[] =
    this.buildTypeGroups();

  private buildTypeGroups(): { category: string; options: { value: DestinationType; label: string }[] }[] {
    const groups: { category: string; options: { value: DestinationType; label: string }[] }[] = [];
    for (const t of TRANSFORMS) {
      if (!t.destinationType) continue;
      if (t.permissionPrefix && !this.permissions.hasPermission(`${t.permissionPrefix}.view`)) continue;
      const category = t.category ?? 'Other';
      let group = groups.find(g => g.category === category);
      if (!group) {
        group = { category, options: [] };
        groups.push(group);
      }
      group.options.push({ value: t.destinationType, label: t.name });
    }
    return groups;
  }

  ngOnInit(): void {
    this.load();
    this._loadUsedInWorkflowIds();
  }

  private _loadUsedInWorkflowIds(): void {
    this.svc.getUsedInWorkflowIds().subscribe({
      next: ids => this.usedInWorkflowIds.set(new Set(ids)),
      error: () => this.usedInWorkflowIds.set(new Set()),
    });
  }

  isUsedInWorkflow(item: DestinationConfigurationDto): boolean {
    return this.usedInWorkflowIds().has(item.id);
  }

  load(): void {
    this.loading.set(true);
    const actionOnDir = this.actionOnSortDirection();
    this.svc
      .getPaged({
        search: this.searchQuery() || undefined,
        destinationType: this.typeFilter() || undefined,
        isEnabled: this.statusFilter() === '' ? undefined : this.statusFilter() === 'true',
        // "Action on" and the regular column sort are mutually exclusive — see onSort/toggleActionOnSort,
        // each clears the other's state, so exactly one of the two is ever active here.
        sortBy: actionOnDir ? 'actionOn' : this.sortColumn(),
        sortOrder: actionOnDir ?? this.sortDirection(),
        page: this.pageIndex() + 1,
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: page => {
          this.items.set(page.items);
          this.totalCount.set(page.totalCount);
          this._loadHistoryFlags(page.items);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.toast.error('Failed to load destination connections.');
        },
      });
  }

  private _loadHistoryFlags(items: DestinationConfigurationDto[]): void {
    if (items.length === 0) {
      this.historyById.set({});
      return;
    }
    forkJoin(
      items.map(item =>
        this.svc.hasExecutionHistory(item.id).pipe(
          map(res => [item.id, res.hasExecutionHistory] as const),
          catchError(() => of([item.id, false] as const)),
        ),
      ),
    ).subscribe(pairs => this.historyById.set(Object.fromEntries(pairs)));
  }

  hasHistory(item: DestinationConfigurationDto): boolean {
    return this.historyById()[item.id] ?? false;
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.pageIndex.set(0);
    this.load();
  }

  onTypeFilterChange(val: string): void {
    this.typeFilter.set(val as DestinationType | '');
    this.pageIndex.set(0);
    this.load();
  }

  onStatusFilterChange(val: string): void {
    this.statusFilter.set(val as '' | 'true' | 'false');
    this.pageIndex.set(0);
    this.load();
  }

  onSort(column: DestinationSortColumn): void {
    this.clearActionOnSort();
    if (this.sortColumn() === column) {
      this.sortDirection.update(d => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.pageIndex.set(0);
    this.load();
  }

  reset(): void {
    this.searchQuery.set('');
    this.typeFilter.set('');
    this.statusFilter.set('');
    this.sortColumn.set('name');
    this.sortDirection.set('asc');
    this.clearActionOnSort();
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  openNew(): void {
    if (!this.actionGuard.ensure(this.CREATE_CODES, 'You do not have permission to create destination connections.')) return;
    this._openDialog({ mode: 'create' }, 'Destination connection created.');
  }

  openEdit(item: DestinationConfigurationDto): void {
    const mode = this.hasHistory(item) ? 'view' : 'edit';
    if (mode === 'edit' && !this.actionGuard.ensure(this.editCode(item), `You do not have permission to edit this ${item.destinationType} destination connection.`)) return;
    this._openDialog({ mode, destination: item }, 'Destination connection updated.');
  }

  private _openDialog(data: DestinationConnectionDialogData, successMessage: string): void {
    this.dialog
      .open(DestinationConnectionDialogComponent, {
        width: '640px',
        disableClose: true,
        restoreFocus: false,
        data,
      })
      .afterClosed()
      .subscribe(result => {
        if (result) {
          this.toast.success(successMessage);
          this.load();
        }
      });
  }

  confirmDelete(item: DestinationConfigurationDto): void {
    if (this.hasHistory(item) || this.isUsedInWorkflow(item)) return;
    if (!this.actionGuard.ensure(this.deleteCodes(item), 'You do not have permission to delete this destination connection.', 'all')) return;

    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Destination Connection',
          message: `Are you sure you want to delete "${item.name}"? This action cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(item.id).subscribe({
          next: () => {
            this.toast.success(`"${item.name}" deleted.`);
            this.load();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete the destination connection.';
            this.toast.error(message);
          },
        });
      });
  }
}
