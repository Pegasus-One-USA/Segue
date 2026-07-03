// user-management/pages/user-detail/user-detail.component.ts
import {
  Component, OnInit, OnDestroy, signal, inject, Input,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { Subject, takeUntil } from 'rxjs';

import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import { MatDialogModule, MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatCardModule } from '@angular/material/card';
import { MatBadgeModule } from '@angular/material/badge';

import { IUserService } from '../../../auth/services/i-user.service';
import { AuthService } from '../../../auth/services/auth.service';
import {
  User, UserRole, Permission,
} from '../../../auth/models/user.model';
import { ROLE_CONFIG } from '../user-list/user-list.component';
import { EditUserDialogComponent } from '../../dialogs/edit-user-dialog/edit-user-dialog.component';
import { AssignRolesDialogComponent } from '../../dialogs/assign-roles-dialog/assign-roles-dialog.component';
import { UserPermissionOverridesComponent } from './user-permission-overrides.component';

@Component({
  selector: 'app-user-detail',
  standalone: true,
  imports: [
    CommonModule,
    MatTabsModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatMenuModule,
    MatDialogModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatDividerModule,
    MatCardModule,
    MatBadgeModule,
    UserPermissionOverridesComponent,
  ],
  templateUrl: './user-detail.component.html',
  styleUrls: ['./user-detail.component.scss'],
})
export class UserDetailComponent implements OnInit, OnDestroy {
  @Input() id!: string;

  private readonly userService = inject(IUserService);
  readonly authService         = inject(AuthService);
  private readonly dialog      = inject(MatDialog);
  private readonly snackBar    = inject(MatSnackBar);
  private readonly router      = inject(Router);

  private readonly destroy$ = new Subject<void>();

  // ─── State signals ────────────────────────────────────────────────────────
  user      = signal<User | null>(null);
  loading   = signal(true);
  activeTab = signal(0);

  readonly roleConfig = ROLE_CONFIG;

  // ─── Lifecycle ────────────────────────────────────────────────────────────
  ngOnInit(): void {
    if (this.id) {
      this.loadUser(this.id);
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ─── Data loading ─────────────────────────────────────────────────────────
  loadUser(id: string): void {
    this.loading.set(true);
    this.userService.getUser(id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: u => {
          this.user.set(u);
          this.loading.set(false);
        },
        error: (err: {message?: string}) => {
          this.loading.set(false);
          this.snackBar.open(err?.message ?? 'Failed to load user.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  // ─── Dialog actions ───────────────────────────────────────────────────────
  openEditDialog(): void {
    const u = this.user();
    if (!u) return;
    const ref = this.dialog.open(EditUserDialogComponent, {
      width: '640px',
      disableClose: true,
      data: { user: u },
    });
    ref.afterClosed().subscribe(result => {
      if (result) this.loadUser(this.id);
    });
  }

  openAssignRolesDialog(): void {
    const u = this.user();
    if (!u) return;
    const ref = this.dialog.open(AssignRolesDialogComponent, {
      width: '680px',
      disableClose: true,
      data: { user: u },
    });
    ref.afterClosed().subscribe(result => {
      if (result) this.loadUser(this.id);
    });
  }

  // ─── Status toggle ────────────────────────────────────────────────────────
  toggleStatus(action: 'enable' | 'disable' | 'suspend'): void {
    const u = this.user();
    if (!u) return;

    const call$ =
      action === 'enable'  ? this.userService.enableUser(u.id)  :
      action === 'disable' ? this.userService.disableUser(u.id) :
                             this.userService.suspendUser(u.id);

    call$.pipe(takeUntil(this.destroy$)).subscribe({
      next: updated => {
        this.user.set(updated);
        const label = action === 'enable' ? 'enabled' : action === 'disable' ? 'disabled' : 'suspended';
        this.snackBar.open(`User "${u.fullName}" ${label}.`, 'Dismiss', { duration: 3000 });
      },
      error: err => {
        this.snackBar.open(err?.message ?? `Failed to ${action} user.`, 'Dismiss', { duration: 4000 });
      },
    });
  }

  resetPassword(): void {
    const u = this.user();
    if (!u) return;
    this.userService.resetUserPassword(u.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`Password reset email sent to "${u.email}".`, 'Dismiss', { duration: 3000 });
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to reset password.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  resendInvitation(): void {
    const u = this.user();
    if (!u) return;
    this.userService.resendInvitation(u.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`Invitation resent to "${u.email}".`, 'Dismiss', { duration: 3000 });
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to resend invitation.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  deleteUser(): void {
    const u = this.user();
    if (!u) return;
    const confirmed = window.confirm(
      `Are you sure you want to delete "${u.fullName}"? This action cannot be undone.`,
    );
    if (!confirmed) return;

    this.userService.deleteUser(u.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.snackBar.open(`User "${u.fullName}" deleted.`, 'Dismiss', { duration: 3000 });
          this.router.navigate(['/user-management']);
        },
        error: (err: {message?: string}) => {
          this.snackBar.open(err?.message ?? 'Failed to delete user.', 'Dismiss', { duration: 4000 });
        },
      });
  }

  goBack(): void {
    this.router.navigate(['/user-management']);
  }

  // ─── Display helpers ──────────────────────────────────────────────────────
  formatDate(d?: string): string {
    if (!d) return '—';
    return new Date(d).toLocaleDateString('en-US', {
      year: 'numeric', month: 'long', day: 'numeric',
    });
  }

  formatDateTime(d?: string): string {
    if (!d) return '—';
    return new Date(d).toLocaleString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit',
    });
  }

  getInitials(user: User): string {
    const f = (user.firstName ?? '').charAt(0).toUpperCase();
    const l = (user.lastName  ?? '').charAt(0).toUpperCase();
    return f + l || user.email.charAt(0).toUpperCase();
  }

  getRoleConfig(role: UserRole) {
    return ROLE_CONFIG[role] ?? { label: role, color: '#64748B', bg: '#F1F5F9' };
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

  getStatusClass(status: string): string {
    const map: Record<string, string> = {
      active:    'status-active',
      inactive:  'status-inactive',
      suspended: 'status-suspended',
      pending:   'status-pending',
    };
    return map[status] ?? 'status-inactive';
  }

  groupPermissions(perms: Permission[]): Map<string, Permission[]> {
    const groups = new Map<string, Permission[]>();
    for (const p of perms) {
      const list = groups.get(p.resource) ?? [];
      list.push(p);
      groups.set(p.resource, list);
    }
    return groups;
  }

  getPermissionsGrouped(user: User): { resource: string; permissions: Permission[] }[] {
    const groups = this.groupPermissions(user.permissions ?? []);
    return Array.from(groups.entries()).map(([resource, permissions]) => ({ resource, permissions }));
  }
}
