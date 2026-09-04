// user-management/pages/user-list/user-list.component.ts
import {
  Component, OnInit, OnDestroy, signal, computed, inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subject, debounceTime, distinctUntilChanged, takeUntil } from 'rxjs';

import { MatTableModule } from '@angular/material/table';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatDialogModule, MatDialog } from '@angular/material/dialog';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';

import { IUserService } from '../../../auth/services/i-user.service';
import { AuthService } from '../../../auth/services/auth.service';
import {
  User, UserRole, UserStatus, UserQueryParams, InviteResult,
} from '../../../auth/models/user.model';

import { EditUserDialogComponent } from '../../dialogs/edit-user-dialog/edit-user-dialog.component';
import { InviteUserDialogComponent } from '../../dialogs/invite-user-dialog/invite-user-dialog.component';
import { AssignRolesDialogComponent } from '../../dialogs/assign-roles-dialog/assign-roles-dialog.component';
import { InviteResultDialogComponent } from '../../dialogs/invite-result-dialog/invite-result-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';
import { ToastService } from '../../../services/toast.service';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

export const ROLE_CONFIG: Record<UserRole, { label: string; color: string; bg: string }> = {
  'SuperAdmin': { label: 'Super Admin', color: '#5B21B6', bg: '#EDE9FE' },
  'Admin':      { label: 'Admin',       color: '#1D4ED8', bg: '#DBEAFE' },
  'Operations': { label: 'Operations',  color: '#00A89D', bg: '#E6F9F7' },
  'Audit':      { label: 'Audit',       color: '#059669', bg: '#ECFDF5' },
};

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    PaginationBarComponent,
    MatSortModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatDialogModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatDividerModule,
    HideWithoutPermissionDirective,
  ],
  templateUrl: './user-list.component.html',
  styleUrls: ['./user-list.component.scss'],
})
export class UserListComponent implements OnInit, OnDestroy {
  private readonly userService = inject(IUserService);
  readonly authService         = inject(AuthService);
  private readonly dialog      = inject(MatDialog);
  private readonly customDialog = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly router      = inject(Router);
  private readonly actionGuard = inject(PermissionActionGuard);

  private readonly destroy$      = new Subject<void>();
  private readonly searchSubject = new Subject<string>();

  // ─── State signals ────────────────────────────────────────────────────────
  users   = signal<User[]>([]);
  total   = signal(0);
  page    = signal(1);
  perPage = signal(10);
  loading = signal(false);

  // ─── Filter signals ───────────────────────────────────────────────────────
  search       = signal('');
  roleFilter   = signal<UserRole | ''>('');
  statusFilter = signal<UserStatus | ''>('');
  sortBy       = signal('createdAt');
  sortOrder    = signal<'asc' | 'desc'>('desc');

  // ─── Computed stats ───────────────────────────────────────────────────────
  totalUsers        = computed(() => this.total());
  activeUsers        = computed(() => this.users().filter(u => u.status === 'active').length);
  inactiveUsers      = computed(() => this.users().filter(u => u.status !== 'active').length);
  mustChangePwdUsers = computed(() => this.users().filter(u => u.mustChangePassword).length);

  readonly displayedColumns = ['actions', 'avatar', 'name', 'roles', 'status', 'loginType', 'lastLogin', 'actionBy', 'actionOn'];

  // Seeded with the 4 built-ins so the dropdown isn't empty while getRoles() is in flight —
  // replaced with the real role list (including any custom roles) once it loads, see ngOnInit.
  readonly roleOptions = signal<UserRole[]>(['SuperAdmin', 'Admin', 'Operations', 'Audit']);

  readonly statusOptions: UserStatus[] = ['active', 'inactive', 'pending'];
  readonly roleConfig = ROLE_CONFIG;

  // ─── Permission gating ──────────────────────────────────────────────────────
  // PermissionGroup/PermissionAction/permissionCode are auto-generated from the backend's own
  // enums (see scripts/generate-permissions.mjs) — exposed as instance fields because template
  // expressions can't reach a plain imported enum/function directly. No permission code in this
  // file is a hand-typed 'group.action' string literal; every check goes through permissionCode().
  protected readonly PermissionGroup = PermissionGroup;
  protected readonly PermissionAction = PermissionAction;
  protected readonly permissionCode = permissionCode;

  private isAdmin(): boolean            { return this.authService.isAdmin(); }
  canInvite     = (): boolean => this.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.User, PermissionAction.Invite));
  canToggle     = (): boolean => this.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.User, PermissionAction.Deactivate));
  canEdit       = (): boolean => this.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.User, PermissionAction.Edit));
  canAssignRole = (): boolean => this.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.Role, PermissionAction.Assign));
  canDelete     = (): boolean => this.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.User, PermissionAction.Delete));

  // Compared by email, not id: the list's user.id is the real database GUID, but
  // authService.currentUser().id is derived from the JWT's external/oid claim (see
  // buildUserFromJwt) — a different identity space that never matches the GUID. Email is the
  // one identifier populated consistently on both sides.
  isSelf = (user: User): boolean => {
    const email = this.authService.currentUser()?.email;
    return !!email && email.toLowerCase() === user.email?.toLowerCase();
  };

  // ─── Lifecycle ────────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.searchSubject.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntil(this.destroy$),
    ).subscribe(query => {
      this.search.set(query);
      this.page.set(1);
      this.loadUsers();
    });

    this.loadUsers();

    this.userService.getRoles()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: roles => this.roleOptions.set(roles.map(r => r.name)),
        error: () => { /* keep the built-in fallback list */ },
      });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ─── Data loading ─────────────────────────────────────────────────────────
  loadUsers(): void {
    this.loading.set(true);
    const params: UserQueryParams = {
      page:      this.page(),
      perPage:   this.perPage(),
      sortBy:    this.sortBy(),
      sortOrder: this.sortOrder(),
    };
    if (this.search())       params.search = this.search();
    if (this.roleFilter())   params.role   = this.roleFilter() as UserRole;
    if (this.statusFilter()) params.status = this.statusFilter() as UserStatus;

    this.userService.getUsers(params)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: res => {
          this.users.set(res.data);
          this.total.set(res.total);
          this.loading.set(false);
        },
        error: (err: { status?: number; message?: string }) => {
          this.loading.set(false);
          this.users.set([]);
          this.total.set(0);
          const msg = err?.status === 403
            ? 'You do not have permission to view users.'
            : err?.message ?? 'Failed to load users.';
          this.toast.error(msg);
        },
      });
  }

  // ─── Filter / pagination / sort ──────────────────────────────────────────
  onSearch(query: string): void { this.searchSubject.next(query); }

  onFilterChange(): void {
    this.page.set(1);
    this.loadUsers();
  }

  onPageChange(e: PageChangeEvent): void {
    this.page.set(e.pageIndex + 1);
    this.perPage.set(e.pageSize);
    this.loadUsers();
  }

  onSortChange(s: Sort): void {
    if (s.direction) {
      this.sortBy.set(s.active);
      this.sortOrder.set(s.direction);
    } else {
      this.sortBy.set('createdAt');
      this.sortOrder.set('desc');
    }
    this.loadUsers();
  }

  // ─── Dialogs ─────────────────────────────────────────────────────────────
  openEditDialog(user: User): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Edit), 'You do not have permission to edit users.')) return;
    const ref = this.customDialog.open<EditUserDialogComponent, { user: User }, User>(EditUserDialogComponent, {
      width: '640px', disableClose: true, data: { user },
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  openInviteDialog(): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Invite), 'You do not have permission to invite users.')) return;
    const ref = this.customDialog.open<InviteUserDialogComponent, unknown, InviteResult>(InviteUserDialogComponent, {
      width: '560px', disableClose: true,
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  openAssignRolesDialog(user: User): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Assign), 'You do not have permission to assign roles.')) return;
    // The list row's User (mapListDto) never carries full Role objects (id/permissions) — only
    // the role name string — so the dialog can't tell which role is currently assigned. Fetch the
    // full detail (mapDetailDto) first so isAssigned()/the allocation preview have real role ids.
    this.userService.getUser(user.id).subscribe({
      next: fullUser => {
        const ref = this.customDialog.open<AssignRolesDialogComponent, { user: User }, User>(AssignRolesDialogComponent, {
          width: '820px', disableClose: true, data: { user: fullUser },
        });
        ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
      },
      error: () => this.toast.error('Failed to load user details.'),
    });
  }

  // ─── User actions ─────────────────────────────────────────────────────────
  deleteUser(user: User): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Delete), 'You do not have permission to delete users.')) return;
    if (this.isSelf(user)) {
      this.toast.error('You cannot delete your own account.');
      return;
    }
    const ref = this.customDialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '400px',
      data: {
        title:        'Delete user',
        message:      `Are you sure you want to delete "${user.fullName}"? This action cannot be undone.`,
        confirmLabel: 'Delete',
        danger:       true,
      },
    });

    ref.afterClosed().subscribe(confirmed => {
      if (!confirmed) return;
      this.userService.deleteUser(user.id)
        .pipe(takeUntil(this.destroy$))
        .subscribe({
          next: () => {
            this.toast.success(`User "${user.fullName}" deleted.`);
            this.loadUsers();
          },
          error: (err: { status?: number; message?: string }) => {
            this.toast.error(this.errMsg(err, 'Failed to delete user.'));
          },
        });
    });
  }

  toggleStatus(user: User, action: 'enable' | 'disable'): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Deactivate), 'You do not have permission to activate or deactivate users.')) return;
    if (action === 'disable' && this.isSelf(user)) {
      this.toast.error('You cannot deactivate your own account.');
      return;
    }
    const call$ = action === 'enable'
      ? this.userService.enableUser(user.id)
      : this.userService.disableUser(user.id);

    call$.pipe(takeUntil(this.destroy$)).subscribe({
      next: () => {
        const label = action === 'enable' ? 'activated' : 'deactivated';
        this.toast.success(`User "${user.fullName}" ${label}.`);
        this.loadUsers();
      },
      error: (err: { status?: number; message?: string }) => {
        this.toast.error(this.errMsg(err, `Failed to ${action} user.`));
      },
    });
  }

  resendInvitation(user: User): void {
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Invite), 'You do not have permission to invite users.')) return;
    this.userService.resendInvitation(user.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: res => {
          if (res.emailSent) {
            this.toast.success(`Invitation resent to "${user.email}".`);
          } else {
            this.toast.warning(`Invitation token refreshed for "${user.email}", but the email failed to send.`);
          }
          this.customDialog.open<InviteResultDialogComponent, InviteResult, void>(InviteResultDialogComponent, {
            width: '540px', data: res,
          });
        },
        error: (err: { status?: number; message?: string }) => {
          this.toast.error(this.errMsg(err, 'Failed to resend invitation.'));
        },
      });
  }

  private errMsg(err: { status?: number; message?: string }, fallback: string): string {
    if (err?.status === 403) return 'You do not have permission to perform this action.';
    return err?.message ?? fallback;
  }

  navigateToDetail(user: User): void {
    this.router.navigate(['/user-management', user.id]);
  }

  // ─── Display helpers ──────────────────────────────────────────────────────
  getInitials(user: User): string {
    const f = (user.firstName ?? '').charAt(0).toUpperCase();
    const l = (user.lastName  ?? '').charAt(0).toUpperCase();
    return f + l || user.email.charAt(0).toUpperCase();
  }

  getStatusLabel(status: string): string {
    const map: Record<string, string> = {
      active:   'Active',
      inactive: 'Deactivated',
      pending:  'Invited',
    };
    return map[status] ?? status;
  }

  getStatusClass(status: string): string {
    const map: Record<string, string> = {
      active:    'badge-active',
      inactive:  'badge-inactive',
      suspended: 'badge-suspended',
      pending:   'badge-queued',
    };
    return map[status] ?? 'badge-inactive';
  }

  getRoleConfig(role: UserRole) {
    return ROLE_CONFIG[role] ?? { label: role, color: '#64748B', bg: '#F1F5F9' };
  }

  getRoleNames(user: User): string[] {
    return (user as any)._globalRoleNames ?? [user.role];
  }

  formatDate(d?: string): string {
    if (!d) return '—';
    return new Date(d).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }

  getAvatarColor(user: User): string {
    const colors = [
      '#00A89D', '#5B21B6', '#1D4ED8', '#0369A1',
      '#0891B2', '#059669', '#D97706', '#DC2626',
    ];
    let hash = 0;
    for (const ch of user.email) hash = (hash * 31 + ch.charCodeAt(0)) & 0xffffffff;
    return colors[Math.abs(hash) % colors.length];
  }

  getLoginTypeLabel(type: string): string {
    const map: Record<string, string> = { local: 'Local', sso: 'SSO', oauth: 'OAuth' };
    return map[type] ?? type;
  }
}
