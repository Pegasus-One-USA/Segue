import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { DialogService } from '../../../core/services/dialog.service';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { IRoleService } from '../../services/i-role.service';
import { Role, SUPER_ADMIN_ROLE_NAME } from '../../../auth/models/user.model';
import { RoleDialogComponent } from '../../dialogs/role-dialog/role-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { AuthService } from '../../../auth/services/auth.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';

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
    MatMenuModule,
    PaginationBarComponent,
    HideWithoutPermissionDirective,
  ],
  templateUrl: './role-list.component.html',
  styleUrls: ['./role-list.component.scss'],
})
export class RoleListComponent implements OnInit {
  private readonly svc         = inject(IRoleService);
  private readonly customDialog = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly router      = inject(Router);
  readonly authService         = inject(AuthService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly destroyRef  = inject(DestroyRef);

  // ─── Permission gating — mirrors user-list.component.ts's established pattern exactly. Exposed
  // as instance fields (template expressions can't reach an imported enum/function directly) and
  // consumed via *appHideWithoutPermission in the template rather than local canX() wrappers, since
  // nothing here needs an isAdmin() OR beyond what the directive already does internally. ──────
  protected readonly PermissionGroup = PermissionGroup;
  protected readonly PermissionAction = PermissionAction;
  protected readonly permissionCode = permissionCode;

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(10);
  readonly loading     = signal(true);
  readonly totalCount  = signal(0);

  readonly roles = signal<Role[]>([]);

  readonly displayedCols = ['actions', 'name', 'description', 'permissions', 'actionBy', 'actionOn'];

  /** Only one sortable column today — "Action on" (createdAt, or modifiedOnUtc when later). */
  readonly actionOnSortDirection = signal<'asc' | 'desc' | null>(null);

  private readonly searchChanged = new Subject<string>();

  toggleActionOnSort(): void {
    this.actionOnSortDirection.set(this.actionOnSortDirection() === 'desc' ? 'asc' : 'desc');
    this.pageIndex.set(0);
    this.loadRoles();
  }

  ngOnInit(): void {
    this.searchChanged.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      this.pageIndex.set(0);
      this.loadRoles();
    });

    this.loadRoles();
  }

  loadRoles(): void {
    this.loading.set(true);
    const direction = this.actionOnSortDirection();
    this.svc.getPagedRoles({
      search: this.searchQuery().trim() || undefined,
      sortDescending: direction ? direction === 'desc' : undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: result => {
        this.roles.set(result.items);
        this.totalCount.set(result.totalCount);
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
    this.searchChanged.next(val);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.loadRoles();
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadRoles();
  }

  openAdd(): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Create), 'You do not have permission to create roles.')) return;
    this.customDialog
      .open<RoleDialogComponent, {}, boolean>(RoleDialogComponent, {
        width: '560px',
        disableClose: true,
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
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Edit), 'You do not have permission to edit roles.')) return;
    this.customDialog
      .open<RoleDialogComponent, { role: Role }, boolean>(RoleDialogComponent, {
        width: '560px',
        disableClose: true,
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

  /** Only SuperAdmin is non-deletable — every other role (built-in or custom) can be deleted once no
   *  user holds it (enforced server-side too). Distinct from `isSystemRole`, which still locks
   *  name/description editing for Admin/Operations/Audit but no longer blocks deletion. */
  isSuperAdmin(role: Role): boolean {
    return role.name === SUPER_ADMIN_ROLE_NAME;
  }

  confirmDelete(role: Role): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Delete), 'You do not have permission to delete roles.')) return;
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
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
