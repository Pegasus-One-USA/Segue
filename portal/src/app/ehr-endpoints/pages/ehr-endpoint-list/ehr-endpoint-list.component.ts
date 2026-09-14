import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { IEhrEndpointService } from '../../services/i-ehr-endpoint.service';
import { EhrEndpoint, EhrVendor } from '../../models/ehr-endpoint.model';
import { EhrEndpointDialogComponent, EhrEndpointDialogData } from '../../dialogs/ehr-endpoint-dialog/ehr-endpoint-dialog.component';
import { sourceSystemDisplayName } from '../../../data/source-system-display-names.data';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { DialogService } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-ehr-endpoint-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatMenuModule,
    HideWithoutPermissionDirective,
  ],
  templateUrl: './ehr-endpoint-list.component.html',
  styleUrls: ['./ehr-endpoint-list.component.scss'],
})
export class EhrEndpointListComponent implements OnInit {
  private readonly svc         = inject(IEhrEndpointService);
  private readonly dialog      = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly permissions = inject(PermissionService);
  private readonly destroyRef  = inject(DestroyRef);

  /** Whether the row's 3-dot menu has anything in it at all — a view-only role (e.g. Audit) with
   *  neither ehrendpoints.edit nor ehrendpoints.delete should never see an empty kebab menu. */
  hasRowMenu(): boolean {
    return this.permissions.hasPermission('ehrendpoints.edit') || this.permissions.hasPermission('ehrendpoints.delete');
  }

  readonly searchQuery  = signal('');
  /** The "Source" dropdown — filters on the row's Vendor. */
  readonly sourceFilter = signal<EhrVendor | ''>('');
  /** The "Status" dropdown, as the raw `<select>` value; '' = All. Mapped to the API's `isActive` in load(). */
  readonly statusFilter = signal<'' | 'true' | 'false'>('');
  readonly pageIndex    = signal(0);
  readonly pageSize     = signal(10);
  readonly loading      = signal(true);
  readonly totalCount   = signal(0);

  readonly endpoints = signal<EhrEndpoint[]>([]);

  readonly displayedCols = ['actions', 'name', 'vendor', 'endpointType', 'fhirBaseUrl', 'status', 'actionBy', 'actionOn'];

  /** Only one sortable column today — "Action on" (createdOnUtc, or modifiedOnUtc when later). */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  private readonly searchChanged = new Subject<string>();

  /** Vendors that actually have endpoint rows, from the server's last response — not the full roster of every
   *  vendor the platform supports. Same facet contract the Workflows and Execution History Source filters use, so
   *  a deployment holding only Epic and eCW endpoints sees two options rather than every SourceSystemType. */
  readonly availableVendors = signal<EhrVendor[]>([]);

  readonly sourceOptions = computed(() =>
    this.availableVendors().map(vendor => ({ value: vendor, label: this.vendorLabel(vendor) }))
  );

  /** Brand name for a vendor — the Vendor badge and the Source filter must agree, so both go through this. */
  vendorLabel(vendor: string | null | undefined): string {
    return sourceSystemDisplayName(vendor);
  }

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
    this.pageIndex.set(0);
    this.loadEndpoints();
  }

  readonly showingFrom = computed(() =>
    this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.totalCount())
  );

  ngOnInit(): void {
    this.searchChanged.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      this.pageIndex.set(0);
      this.loadEndpoints();
    });

    this.loadEndpoints();
  }

  loadEndpoints(): void {
    this.loading.set(true);
    const direction = this.actionOnSortDirection();
    this.svc.getPaged({
      search: this.searchQuery().trim() || undefined,
      vendor: this.sourceFilter() || undefined,
      isActive: this.statusFilter() === '' ? undefined : this.statusFilter() === 'true',
      sortDescending: direction ? direction === 'desc' : undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: result => {
        this.endpoints.set(result.items);
        this.totalCount.set(result.totalCount);
        this.availableVendors.set(result.availableVendors ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load EHR endpoints.');
      },
    });
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.searchChanged.next(val);
  }

  onSourceFilterChange(val: string): void {
    this.sourceFilter.set(val as EhrVendor | '');
    this.pageIndex.set(0);
    this.loadEndpoints();
  }

  onStatusFilterChange(val: string): void {
    this.statusFilter.set(val as '' | 'true' | 'false');
    this.pageIndex.set(0);
    this.loadEndpoints();
  }

  /** Whether any filter is narrowing the list — drives the empty state's "no match" vs "nothing yet" copy,
   *  which previously only considered the search box. */
  readonly hasActiveFilter = computed(() =>
    !!this.searchQuery() || !!this.sourceFilter() || this.statusFilter() !== ''
  );

  reset(): void {
    this.searchQuery.set('');
    this.sourceFilter.set('');
    this.statusFilter.set('');
    this.pageIndex.set(0);
    this.loadEndpoints();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadEndpoints();
  }

  openAdd(): void {
    if (!this.actionGuard.ensure('ehrendpoints.create', 'You do not have permission to create EHR endpoints.')) return;
    this.dialog
      .open<EhrEndpointDialogComponent, EhrEndpointDialogData, boolean>(EhrEndpointDialogComponent, {
        width: '560px',
        disableClose: true,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('EHR endpoint added successfully.');
          this.loadEndpoints();
        }
      });
  }

  openEdit(endpoint: EhrEndpoint): void {
    if (!this.actionGuard.ensure('ehrendpoints.edit', 'You do not have permission to edit EHR endpoints.')) return;
    this.dialog
      .open<EhrEndpointDialogComponent, EhrEndpointDialogData, boolean>(EhrEndpointDialogComponent, {
        width: '560px',
        disableClose: true,
        data: { endpoint },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('EHR endpoint updated successfully.');
          this.loadEndpoints();
        }
      });
  }

  confirmDelete(endpoint: EhrEndpoint): void {
    if (!this.actionGuard.ensure('ehrendpoints.delete', 'You do not have permission to delete EHR endpoints.')) return;
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete EHR Endpoint',
          message: `Are you sure you want to delete "${endpoint.name}"? This action cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(endpoint.id).subscribe({
          next: () => {
            this.toast.success(`"${endpoint.name}" deleted.`);
            this.loadEndpoints();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete EHR endpoint.';
            this.toast.error(message);
          },
        });
      });
  }
}
