// user-management/pages/user-list/user-list.component.ts
import {
  Component, OnInit, OnDestroy, signal, computed, inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subject, debounceTime, distinctUntilChanged, takeUntil } from 'rxjs';

import { MatTableModule } from '@angular/material/table';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatDialogModule, MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';

import { IUserService } from '../../../auth/services/i-user.service';
import { AuthService } from '../../../auth/services/auth.service';
import {
  User, UserRole, UserStatus, UserQueryParams,
} from '../../../auth/models/user.model';

import { CreateUserDialogComponent } from '../../dialogs/create-user-dialog/create-user-dialog.component';
import { EditUserDialogComponent } from '../../dialogs/edit-user-dialog/edit-user-dialog.component';
import { InviteUserDialogComponent } from '../../dialogs/invite-user-dialog/invite-user-dialog.component';
import { AssignRolesDialogComponent } from '../../dialogs/assign-roles-dialog/assign-roles-dialog.component';

export const ROLE_CONFIG: Record<UserRole, { label: string; color: string; bg: string }> = {
  'system-admin':    { label: 'System Admin',    color: '#5B21B6', bg: '#EDE9FE' },
  'tenant-admin':    { label: 'Tenant Admin',    color: '#1D4ED8', bg: '#DBEAFE' },
  'developer':       { label: 'Developer',       color: '#0369A1', bg: '#E0F2FE' },
  'pipeline-editor': { label: 'Pipeline Editor', color: '#00A89D', bg: '#E6F9F7' },
  'reviewer':        { label: 'Reviewer',        color: '#0891B2', bg: '#E0F9FF' },
  'auditor':         { label: 'Auditor',         color: '#059669', bg: '#ECFDF5' },
  'analyst':         { label: 'Analyst',         color: '#D97706', bg: '#FEF3C7' },
  'viewer':          { label: 'Viewer',          color: '#64748B', bg: '#F1F5F9' },
};

@Component({
  selector: 'app-user-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatTableModule,
    MatPaginatorModule,
    MatSortModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatDialogModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatDividerModule,
  ],
  templateUrl: './user-list.component.html',
  styleUrls: ['./user-list.component.scss'],
})
export class UserListComponent implements OnInit, OnDestroy {
  private readonly userService = inject(IUserService);
  readonly authService         = inject(AuthService);
  private readonly dialog      = inject(MatDialog);
  private readonly snackBar    = inject(MatSnackBar);
  private readonly router      = inject(Router);

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

  readonly displayedColumns = ['avatar', 'name', 'roles', 'status', 'loginType', 'lastLogin', 'actions'];

  readonly roleOptions: UserRole[] = [
    'system-admin', 'tenant-admin', 'developer', 'pipeline-editor',
    'reviewer', 'auditor', 'analyst', 'viewer',
  ];

  readonly statusOptions: UserStatus[] = ['active', 'inactive', 'suspended', 'pending'];
  readonly roleConfig = ROLE_CONFIG;

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
        error: (err: {message?: string}) => {
          this.loading.set(false);
          this.snackBar.open(err?.message ?? 'Failed to load users.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  // ─── Filter / pagination / sort ──────────────────────────────────────────
  onSearch(query: string): void { this.searchSubject.next(query); }

  onFilterChange(): void {
    this.page.set(1);
    this.loadUsers();
  }

  onPageChange(e: PageEvent): void {
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
  openCreateDialog(): void {
    const ref = this.dialog.open(CreateUserDialogComponent, {
      width: '640px', disableClose: true, restoreFocus: false,
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  openEditDialog(user: User): void {
    const ref = this.dialog.open(EditUserDialogComponent, {
      width: '640px', disableClose: true, restoreFocus: false, data: { user },
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  openInviteDialog(): void {
    const ref = this.dialog.open(InviteUserDialogComponent, {
      width: '560px', disableClose: true, restoreFocus: false,
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  openAssignRolesDialog(user: User): void {
    const ref = this.dialog.open(AssignRolesDialogComponent, {
      width: '680px', disableClose: true, restoreFocus: false, data: { user },
    });
    ref.afterClosed().subscribe(result => { if (result) this.loadUsers(); });
  }

  // ─── User actions ─────────────────────────────────────────────────────────
  deleteUser(user: User): void {
    const confirmed = window.confirm(
      `Are you sure you want to delete "${user.fullName}"? This action cannot be undone.`,
    );
    if (!confirmed) return;

    this.userService.deleteUser(user.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`User "${user.fullName}" deleted.`, 'Dismiss', { duration: 3000 });
          this.loadUsers();
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to delete user.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  toggleStatus(user: User, action: 'enable' | 'disable' | 'suspend'): void {
    const call$ =
      action === 'enable'  ? this.userService.enableUser(user.id)  :
      action === 'disable' ? this.userService.disableUser(user.id) :
                             this.userService.suspendUser(user.id);

    call$.pipe(takeUntil(this.destroy$)).subscribe({
      next: updated => {
        this.users.update(list => list.map(u => u.id === updated.id ? updated : u));
        const label = action === 'enable' ? 'enabled' : action === 'disable' ? 'disabled' : 'suspended';
        this.snackBar.open(`User "${user.fullName}" ${label}.`, 'Dismiss', { duration: 3000 });
      },
      error: err => {
        this.snackBar.open(err?.message ?? `Failed to ${action} user.`, 'Dismiss', { duration: 4000 });
      },
    });
  }

  resendInvitation(user: User): void {
    this.userService.resendInvitation(user.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`Invitation resent to "${user.email}".`, 'Dismiss', { duration: 3000 });
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to resend invitation.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  resetPassword(user: User): void {
    this.userService.resetUserPassword(user.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`Password reset email sent to "${user.email}".`, 'Dismiss', { duration: 3000 });
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to reset password.', 'Dismiss', { duration: 4000 });
        },
      });
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

  getStatusClass(status: string): string {
    const map: Record<string, string> = {
      active:    'status-active',
      inactive:  'status-inactive',
      suspended: 'status-suspended',
      pending:   'status-pending',
    };
    return map[status] ?? 'status-inactive';
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
