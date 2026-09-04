import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { MappingProfileService } from '../../services/mapping-profile.service';
import { MappingProfileDto, MappingProfileSortColumn, SortOrder } from '../../models/mapping-profile.model';
import {
  MappingProfileDialogV2Component,
  MappingProfileDialogV2Data,
} from '../../dialogs/mapping-profile-dialog-v2/mapping-profile-dialog-v2.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { SOURCE_CONNECTIONS_ENDPOINTS, DESTINATION_ENDPOINTS } from '../../../core/api-endpoints';
import { FHIR_RESOURCES } from '../../../data/scope-constants.data';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';

interface NamedEntity {
  id: string;
  name: string;
}

/**
 * Standalone admin CRUD for MappingProfile rows (docs/backend/14-mapping-profile-master-screen-plan.md §5.2/5.3),
 * the peer of Source Connections and Destination Connections. Server-side paged/filtered, same shape as
 * DestinationConnectionListComponent. Delete is gated the same way — a profile referenced by at least one
 * workflow can't be deleted (the server independently re-checks and would 409 anyway; this just avoids the
 * round trip and explains why up front).
 */
@Component({
  selector: 'app-mapping-profile-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatMenuModule,
    PaginationBarComponent,
    HideWithoutPermissionDirective,
  ],
  templateUrl: './mapping-profile-list.component.html',
  styleUrls: ['./mapping-profile-list.component.scss'],
})
export class MappingProfileListComponent implements OnInit {
  private readonly svc         = inject(MappingProfileService);
  private readonly http        = inject(HttpClient);
  private readonly customDialog = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly permissions         = inject(PermissionService);

  /** Whether a row's 3-dot menu has anything in it at all — a view-only role (e.g. Audit) with
   *  neither edit nor delete should never see an empty kebab menu. */
  hasRowMenu(): boolean {
    return this.permissions.hasPermission('mappingprofiles.edit')
      || this.permissions.hasPermission('mappingprofiles.delete');
  }

  readonly searchQuery = signal('');
  readonly resourceTypeFilter = signal('');
  readonly statusFilter = signal<'' | 'true' | 'false'>('');
  readonly sortColumn = signal<MappingProfileSortColumn>('name');
  readonly sortDirection = signal<SortOrder>('asc');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly loading = signal(true);

  readonly items = signal<MappingProfileDto[]>([]);
  readonly totalCount = signal(0);
  /** ids currently referenced by at least one workflow — gates Delete (see MappingProfileService.getUsedInWorkflowIds). */
  readonly usedInWorkflowIds = signal<Set<string>>(new Set());

  /** id -> name, so the grid can show human-readable Source/Destination instead of raw guids. */
  readonly sourceNamesById = signal<Record<string, string>>({});
  readonly destinationNamesById = signal<Record<string, string>>({});

  readonly displayedCols = [
    'actions', 'name', 'resourceType', 'source', 'destination', 'destinationObject', 'fieldCount', 'isEnabled', 'actionOn',
  ];

  readonly resourceTypeOptions = FHIR_RESOURCES;

  ngOnInit(): void {
    this.load();
    this._loadNameLookups();
    this._loadUsedInWorkflowIds();
  }

  private _loadNameLookups(): void {
    this.http.get<NamedEntity[]>(SOURCE_CONNECTIONS_ENDPOINTS.list).subscribe({
      next: sources => this.sourceNamesById.set(Object.fromEntries(sources.map(s => [s.id, s.name]))),
      error: () => this.sourceNamesById.set({}),
    });
    this.http.get<NamedEntity[]>(DESTINATION_ENDPOINTS.list).subscribe({
      next: destinations => this.destinationNamesById.set(Object.fromEntries(destinations.map(d => [d.id, d.name]))),
      error: () => this.destinationNamesById.set({}),
    });
  }

  sourceName(id: string): string {
    return this.sourceNamesById()[id] ?? id;
  }

  destinationName(id: string): string {
    return this.destinationNamesById()[id] ?? id;
  }

  private _loadUsedInWorkflowIds(): void {
    this.svc.getUsedInWorkflowIds().subscribe({
      next: ids => this.usedInWorkflowIds.set(new Set(ids)),
      error: () => this.usedInWorkflowIds.set(new Set()),
    });
  }

  isUsedInWorkflow(item: MappingProfileDto): boolean {
    return this.usedInWorkflowIds().has(item.id);
  }

  load(): void {
    this.loading.set(true);
    this.svc
      .getPaged({
        search: this.searchQuery() || undefined,
        resourceType: this.resourceTypeFilter() || undefined,
        isEnabled: this.statusFilter() === '' ? undefined : this.statusFilter() === 'true',
        sortBy: this.sortColumn(),
        sortOrder: this.sortDirection(),
        page: this.pageIndex() + 1,
        pageSize: this.pageSize(),
      })
      .subscribe({
        next: page => {
          this.items.set(page.items);
          this.totalCount.set(page.totalCount);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.toast.error('Failed to load mapping profiles.');
        },
      });
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.pageIndex.set(0);
    this.load();
  }

  onResourceTypeFilterChange(val: string): void {
    this.resourceTypeFilter.set(val);
    this.pageIndex.set(0);
    this.load();
  }

  onStatusFilterChange(val: string): void {
    this.statusFilter.set(val as '' | 'true' | 'false');
    this.pageIndex.set(0);
    this.load();
  }

  onSort(column: MappingProfileSortColumn): void {
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
    this.resourceTypeFilter.set('');
    this.statusFilter.set('');
    this.sortColumn.set('name');
    this.sortDirection.set('asc');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  openNew(): void {
    if (!this.actionGuard.ensure('mappingprofiles.create', 'You do not have permission to create mapping profiles.')) return;
    this._openDialog({ mode: 'create' }, 'Mapping profile created.');
  }

  openEdit(item: MappingProfileDto): void {
    if (!this.actionGuard.ensure('mappingprofiles.edit', 'You do not have permission to edit mapping profiles.')) return;
    this._openDialog({ mode: 'edit', mappingProfile: item }, 'Mapping profile updated.');
  }

  private _openDialog(data: MappingProfileDialogV2Data, successMessage: string): void {
    // V2 (redesigned to match Add Role's polish — see mapping-profile-dialog-v2/): fills the content
    // area edge-to-edge immediately on open, unconditionally — the same static treatment the Source
    // Connection screen always gets, no waiting for the dialog to grow once a resource type is picked.
    // maximize still escalates further, to the true full viewport (sidebar included).
    this.customDialog
      .open<MappingProfileDialogV2Component, MappingProfileDialogV2Data, MappingProfileDto | false>(MappingProfileDialogV2Component, {
        disableClose: true,
        maximizable: true,
        fillContent: true,
        data,
      })
      .afterClosed()
      .subscribe(result => {
        if (result) {
          if (successMessage) this.toast.success(successMessage);
          this.load();
        }
      });
  }

  toggleEnabled(item: MappingProfileDto): void {
    if (!this.actionGuard.ensure('mappingprofiles.deactivate', 'You do not have permission to activate or deactivate mapping profiles.')) return;
    const action = item.isEnabled ? this.svc.deactivate(item.id) : this.svc.activate(item.id);
    action.subscribe({
      next: () => {
        this.toast.success(item.isEnabled ? `"${item.name}" deactivated.` : `"${item.name}" activated.`);
        this.load();
      },
      error: (err: HttpErrorResponse) => {
        this.toast.error(err.error?.message ?? 'Failed to update the mapping profile.');
      },
    });
  }

  confirmDelete(item: MappingProfileDto): void {
    if (this.isUsedInWorkflow(item)) return;
    if (!this.actionGuard.ensure('mappingprofiles.delete', 'You do not have permission to delete mapping profiles.')) return;

    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete Mapping Profile',
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
            const message = err.error?.message ?? 'Failed to delete the mapping profile.';
            this.toast.error(message);
          },
        });
      });
  }
}
