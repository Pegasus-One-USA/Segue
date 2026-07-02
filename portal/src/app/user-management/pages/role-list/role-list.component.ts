import { Component, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { TenantRoleService, CustomRole } from '../../services/tenant-role.service';
import { RoleDialogComponent } from '../../dialogs/role-dialog/role-dialog.component';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-role-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './role-list.component.html',
  styleUrls: ['./role-list.component.scss'],
})
export class RoleListComponent {
  private readonly svc    = inject(TenantRoleService);
  private readonly dialog = inject(MatDialog);
  private readonly snack  = inject(MatSnackBar);

  readonly searchQuery  = signal('');
  readonly tenantFilter = signal('');
  readonly pageIndex    = signal(0);
  readonly pageSize     = signal(10);

  readonly tenants       = this.svc.tenants;
  readonly displayedCols = ['index', 'name', 'description', 'tenant', 'createdAt', 'actions'];

  readonly filtered = computed(() => {
    const q   = this.searchQuery().toLowerCase().trim();
    const tid = this.tenantFilter();
    return this.svc.roles().filter(r => {
      const matchQ = !q || r.name.toLowerCase().includes(q) || r.description.toLowerCase().includes(q);
      const matchT = !tid || r.tenantId === tid;
      return matchQ && matchT;
    });
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

  onTenantFilter(val: string): void {
    this.tenantFilter.set(val);
    this.pageIndex.set(0);
  }

  reset(): void {
    this.searchQuery.set('');
    this.tenantFilter.set('');
    this.pageIndex.set(0);
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  openAdd(): void {
    this.dialog
      .open(RoleDialogComponent, {
        width: '520px',
        disableClose: true,
        restoreFocus: false,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.snack.open('Role added successfully.', 'Dismiss', { duration: 3000 });
      });
  }

  openEdit(role: CustomRole): void {
    this.dialog
      .open(RoleDialogComponent, {
        width: '520px',
        disableClose: true,
        restoreFocus: false,
        data: { role },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) this.snack.open('Role updated successfully.', 'Dismiss', { duration: 3000 });
      });
  }

  confirmDelete(role: CustomRole): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Role',
          message: `Are you sure you want to delete role "${role.name}"? This action cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteRole(role.id);
        this.snack.open(`Role "${role.name}" deleted.`, 'Dismiss', { duration: 3000 });
      });
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
