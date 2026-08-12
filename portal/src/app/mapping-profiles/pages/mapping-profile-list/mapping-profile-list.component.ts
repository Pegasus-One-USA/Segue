import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MappingProfileService } from '../../services/mapping-profile.service';
import { MappingProfileDto, MappingProfileSortColumn, SortOrder } from '../../models/mapping-profile.model';
import {
  MappingProfileDialogComponent,
  MappingProfileDialogData,
} from '../../dialogs/mapping-profile-dialog/mapping-profile-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { SOURCE_CONNECTIONS_ENDPOINTS, DESTINATION_ENDPOINTS } from '../../../core/api-endpoints';
import { FHIR_RESOURCES } from '../../../data/scope-constants.data';

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
    PaginationBarComponent,
  ],
  templateUrl: './mapping-profile-list.component.html',
  styleUrls: ['./mapping-profile-list.component.scss'],
})
export class MappingProfileListComponent implements OnInit {
  private readonly svc = inject(MappingProfileService);
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(ToastService);

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
    'name', 'resourceType', 'source', 'destination', 'destinationObject', 'fieldCount', 'isEnabled', 'actionOn', 'actions',
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
    this._openDialog({ mode: 'create' }, 'Mapping profile created.');
  }

  openEdit(item: MappingProfileDto): void {
    this._openDialog({ mode: 'edit', mappingProfile: item }, 'Mapping profile updated.');
  }

  private _openDialog(data: MappingProfileDialogData, successMessage: string): void {
    this.dialog
      .open(MappingProfileDialogComponent, {
        // Same "XL modal" size the design system already defines for exactly this kind of large dialog
        // (§11: min(92vw, 1100px) / min(88vh, 740px) — what NodeLibraryDialogComponent itself uses on the
        // workflow-builder canvas) instead of forcing near-fullscreen by default. Reads as an appropriately
        // sized dialog rather than a mostly-empty near-fullscreen one before a resource type is picked; the
        // maximize button (see MappingProfileDialogComponent.toggleMaximize) still covers whoever needs
        // the full 100vw/100vh canvas room.
        width: 'min(92vw, 1100px)',
        height: 'min(88vh, 740px)',
        // maxWidth/maxHeight stay at the full 100vw/100vh (not a tighter cap) — MatDialogConfig's
        // maxWidth/maxHeight are applied once at open and aren't updated by dialogRef.updateSize() later,
        // so a tighter static cap here would silently clamp
        // MappingProfileDialogComponent.toggleMaximize()'s 100vw/100vh fullscreen resize.
        maxWidth: '100vw',
        maxHeight: '100vh',
        disableClose: true,
        restoreFocus: false,
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

    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
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
