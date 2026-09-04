import { Component, DestroyRef, OnInit, inject, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { TenantRoleService, Tenant } from '../../services/tenant-role.service';
import { TenantDialogComponent, TenantDialogData } from '../../dialogs/tenant-dialog/tenant-dialog.component';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-tenant-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './tenant-list.component.html',
  styleUrls: ['./tenant-list.component.scss'],
})
export class TenantListComponent implements OnInit {
  private readonly svc        = inject(TenantRoleService);
  private readonly dialog     = inject(DialogService);
  private readonly toast      = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly totalCount  = signal(0);
  readonly tenants     = signal<Tenant[]>([]);

  readonly displayedCols = ['index', 'name', 'code', 'createdAt', 'actions'];

  private readonly searchChanged = new Subject<string>();

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
      this.loadTenants();
    });

    this.loadTenants();
  }

  loadTenants(): void {
    this.svc.getPagedTenants(this.searchQuery().trim() || undefined, this.pageIndex() + 1, this.pageSize())
      .subscribe({
        next: result => {
          this.tenants.set(result.items);
          this.totalCount.set(result.totalCount);
        },
        error: () => this.toast.error('Failed to load tenants.'),
      });
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.searchChanged.next(val);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.loadTenants();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadTenants();
  }

  openAdd(): void {
    this.dialog
      .open<TenantDialogComponent, TenantDialogData, boolean>(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Tenant added successfully.');
          this.loadTenants();
        }
      });
  }

  openEdit(tenant: Tenant): void {
    this.dialog
      .open<TenantDialogComponent, TenantDialogData, boolean>(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        data: { tenant },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Tenant updated successfully.');
          this.loadTenants();
        }
      });
  }

  confirmDelete(tenant: Tenant): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete Tenant',
          message: `Are you sure you want to delete tenant "${tenant.name}"? This cannot be undone. A tenant that still has users cannot be deleted.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteTenant(tenant.id).subscribe({
          next: () => {
            this.toast.success(`Tenant "${tenant.name}" deleted.`);
            this.loadTenants();
          },
          error: (err) => this.toast.error(
            'Delete failed', err?.error?.message ?? `Could not delete tenant "${tenant.name}".`),
        });
      });
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
