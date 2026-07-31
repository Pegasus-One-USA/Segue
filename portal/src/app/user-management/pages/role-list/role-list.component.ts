import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IRoleService } from '../../services/i-role.service';
import { Role } from '../../../auth/models/user.model';
import { RoleDialogComponent } from '../../dialogs/role-dialog/role-dialog.component';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

@Component({
  selector: 'app-role-list',
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
  ],
  templateUrl: './role-list.component.html',
  styleUrls: ['./role-list.component.scss'],
})
export class RoleListComponent implements OnInit {
  private readonly svc    = inject(IRoleService);
  private readonly dialog = inject(MatDialog);
  private readonly toast  = inject(ToastService);
  private readonly router = inject(Router);

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);

  readonly roles = signal<Role[]>([]);

  readonly displayedCols = ['index', 'name', 'description', 'permissions', 'actionBy', 'actionOn', 'actions'];

  /** Only one sortable column today — "Action on" (createdAt, or modifiedOnUtc when later). */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
  }

  private static actionOnOf(r: Role): number {
    const value = r.modifiedOnUtc || r.createdAt;
    return value ? new Date(value).getTime() : 0;
  }

  readonly filtered = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    const rows = !q ? this.roles() : this.roles().filter(r =>
      r.displayName.toLowerCase().includes(q) || r.description.toLowerCase().includes(q)
    );

    const direction = this.actionOnSortDirection();
    if (!direction) return rows;
    const sorted = [...rows].sort((a, b) => RoleListComponent.actionOnOf(a) - RoleListComponent.actionOnOf(b));
    return direction === 'desc' ? sorted.reverse() : sorted;
  });

  readonly paginated = computed(() => {
    const start = this.pageIndex() * this.pageSize();
    return this.filtered().slice(start, start + this.pageSize());
  });

  ngOnInit(): void {
    this.loadRoles();
  }

  loadRoles(): void {
    this.loading.set(true);
    this.svc.getRoles().subscribe({
      next: roles => {
        this.roles.set(roles);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        this.toast.error(err.message || 'Failed to load roles.');
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

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
  }

  openAdd(): void {
    this.dialog
      .open(RoleDialogComponent, {
        width: '560px',
        disableClose: true,
        restoreFocus: false,
        data: {},
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Role added successfully.');
          this.loadRoles();
        }
      });
  }

  openEdit(role: Role): void {
    this.dialog
      .open(RoleDialogComponent, {
        width: '560px',
        disableClose: true,
        restoreFocus: false,
        data: { role },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Role updated successfully.');
          this.loadRoles();
        }
      });
  }

  openPermissions(role: Role): void {
    this.router.navigate(['/user-management/roles', role.id, 'permissions']);
  }

  confirmDelete(role: Role): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        restoreFocus: false,
        data: {
          title: 'Delete Role',
          message: `Are you sure you want to delete role "${role.displayName}"? This action cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteRole(role.id).subscribe({
          next: () => {
            this.toast.success(`Role "${role.displayName}" deleted.`);
            this.loadRoles();
          },
          error: (err: HttpErrorResponse) => {
            const message = typeof err.error?.title === 'string' ? err.error.title : err.message || 'Failed to delete role.';
            this.toast.error(message);
          },
        });
      });
  }
}
