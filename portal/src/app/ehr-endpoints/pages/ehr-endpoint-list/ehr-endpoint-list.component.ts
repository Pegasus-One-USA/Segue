import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IEhrEndpointService } from '../../services/i-ehr-endpoint.service';
import { EhrEndpoint } from '../../models/ehr-endpoint.model';
import { EhrEndpointDialogComponent } from '../../dialogs/ehr-endpoint-dialog/ehr-endpoint-dialog.component';
import { ConfirmDialogComponent } from '../../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

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
  ],
  templateUrl: './ehr-endpoint-list.component.html',
  styleUrls: ['./ehr-endpoint-list.component.scss'],
})
export class EhrEndpointListComponent implements OnInit {
  private readonly svc    = inject(IEhrEndpointService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);

  readonly endpoints = signal<EhrEndpoint[]>([]);

  readonly displayedCols = ['index', 'name', 'vendor', 'endpointType', 'fhirBaseUrl', 'status', 'actionBy', 'actionOn', 'actions'];

  /** Only one sortable column today — "Action on" (createdOnUtc, or modifiedOnUtc when later). */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
  }

  private static actionOnOf(e: EhrEndpoint): number {
    const value = e.modifiedOnUtc || e.createdOnUtc;
    return value ? new Date(value).getTime() : 0;
  }

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    const rows = !q ? this.endpoints() : this.endpoints().filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.vendor.toLowerCase().includes(q) ||
      e.fhirBaseUrl.toLowerCase().includes(q)
    );

    const direction = this.actionOnSortDirection();
    if (!direction) return rows;
    const sorted = [...rows].sort((a, b) => EhrEndpointListComponent.actionOnOf(a) - EhrEndpointListComponent.actionOnOf(b));
    return direction === 'desc' ? sorted.reverse() : sorted;
  });

  readonly paginated = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  readonly showingFrom = computed(() =>
    this.filtered().length === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.filtered().length)
  );

  ngOnInit(): void {
    this.loadEndpoints();
  }

  loadEndpoints(): void {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: endpoints => {
        this.endpoints.set(endpoints);
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
    this.pageIndex.set(0);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  openAdd(): void {
    this.dialog
      .open(EhrEndpointDialogComponent, {
        width: '560px',
        disableClose: true,
        restoreFocus: false,
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
    this.dialog
      .open(EhrEndpointDialogComponent, {
        width: '560px',
        disableClose: true,
        restoreFocus: false,
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
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
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
