import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
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
  private readonly snack  = inject(MatSnackBar);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);

  readonly endpoints = signal<EhrEndpoint[]>([]);

  readonly displayedCols = ['index', 'name', 'vendor', 'endpointType', 'fhirBaseUrl', 'status', 'actions'];

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.endpoints();
    return this.endpoints().filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.vendor.toLowerCase().includes(q) ||
      e.fhirBaseUrl.toLowerCase().includes(q)
    );
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
        this.snack.open('Failed to load EHR endpoints.', 'Dismiss', { duration: 4000 });
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
          this.snack.open('EHR endpoint added successfully.', 'Dismiss', { duration: 3000 });
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
          this.snack.open('EHR endpoint updated successfully.', 'Dismiss', { duration: 3000 });
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
            this.snack.open(`"${endpoint.name}" deleted.`, 'Dismiss', { duration: 3000 });
            this.loadEndpoints();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to delete EHR endpoint.';
            this.snack.open(message, 'Dismiss', { duration: 5000 });
          },
        });
      });
  }
}
