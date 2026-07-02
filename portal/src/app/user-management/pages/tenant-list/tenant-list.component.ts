import { Component, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { TenantRoleService, Tenant } from '../../services/tenant-role.service';
import { TenantDialogComponent } from '../../dialogs/tenant-dialog/tenant-dialog.component';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';

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
export class TenantListComponent {
  private readonly svc    = inject(TenantRoleService);
  private readonly dialog = inject(MatDialog);
  private readonly snack  = inject(MatSnackBar);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);

  readonly displayedCols = ['index', 'name', 'code', 'createdAt', 'actions'];

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.svc.tenants();
    return this.svc.tenants().filter(t =>
      t.name.toLowerCase().includes(q) || t.code.toLowerCase().includes(q)
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
      .open(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        restoreFocus: false,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.snack.open('Tenant added successfully.', 'Dismiss', { duration: 3000 });
      });
  }

  openEdit(tenant: Tenant): void {
    this.dialog
      .open(TenantDialogComponent, {
        width: '480px',
        disableClose: true,
        restoreFocus: false,
        data: { tenant },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.snack.open('Tenant updated successfully.', 'Dismiss', { duration: 3000 });
      });
  }

  confirmDelete(tenant: Tenant): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Tenant',
          message: `Are you sure you want to delete tenant "${tenant.name}"? All associated roles will also be permanently removed.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteTenant(tenant.id);
        this.snack.open(`Tenant "${tenant.name}" deleted.`, 'Dismiss', { duration: 3000 });
      });
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
