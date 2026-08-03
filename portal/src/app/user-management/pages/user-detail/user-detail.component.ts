// user-management/pages/user-detail/user-detail.component.ts
import {
  Component, OnInit, OnDestroy, signal, computed, inject, Input, ViewChild,
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
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatCardModule } from '@angular/material/card';
import { MatBadgeModule } from '@angular/material/badge';

import { IUserService } from '../../../auth/services/i-user.service';
import { AuthService } from '../../../auth/services/auth.service';
import { IRoleService } from '../../services/i-role.service';
import {
  User, UserRole, Permission, PermissionCategory,
} from '../../../auth/models/user.model';
import { ROLE_CONFIG } from '../user-list/user-list.component';
import { EditUserDialogComponent } from '../../dialogs/edit-user-dialog/edit-user-dialog.component';
import { AssignRolesDialogComponent } from '../../dialogs/assign-roles-dialog/assign-roles-dialog.component';
import { ResetPasswordLinkDialogComponent } from '../../dialogs/reset-password-link-dialog/reset-password-link-dialog.component';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';
import { UserPermissionOverridesComponent } from './user-permission-overrides.component';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { ToastService } from '../../../services/toast.service';

// A permission within the effective-permissions preview — same shape as `Permission` plus
// whether the user's roles actually grant it. Mirrors AssignRolesDialogComponent's preview.
interface StatusPermission extends Permission {
  allowed: boolean;
}

interface StatusGroup {
  id: string;
  displayName: string;
  permissions: StatusPermission[];
}

interface StatusCategory {
  id: string;
  displayName: string;
  groups: StatusGroup[];
}

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
export class UserDetailComponent implements OnInit, OnDestroy, HasUnsavedChanges {
  @Input() id!: string;

  // Direct-Permission-Overrides is rendered eagerly inside a mat-tab (not lazy), so this resolves
  // as soon as the view initializes regardless of which tab is active.
  @ViewChild(UserPermissionOverridesComponent) private overridesComponent?: UserPermissionOverridesComponent;

  private readonly userService = inject(IUserService);
  private readonly roleService = inject(IRoleService);
  readonly authService         = inject(AuthService);
  private readonly dialog      = inject(MatDialog);
  private readonly toast       = inject(ToastService);
  private readonly router      = inject(Router);

  private readonly destroy$ = new Subject<void>();

  // ─── State signals ────────────────────────────────────────────────────────
  user      = signal<User | null>(null);
  catalog   = signal<PermissionCategory[]>([]);
  loading   = signal(true);
  activeTab = signal(0);

  readonly roleConfig = ROLE_CONFIG;

  // Compared by email, not id: user().id is the real database GUID, but authService.currentUser().id
  // is derived from the JWT's external/oid claim (see buildUserFromJwt) — a different identity space
  // that never matches the GUID. Email is the one identifier populated consistently on both sides.
  isSelf = computed(() => {
    const email = this.authService.currentUser()?.email;
    return !!email && email.toLowerCase() === this.user()?.email?.toLowerCase();
  });

  // Bypassing a user's second factor entirely is too sensitive to delegate to the general "edit
  // user" permission Admins also hold — the backend enforces this too (SuperAdminOnly policy on
  // POST /users/{id}/mfa/disable); this just keeps the button from being shown to someone who'd
  // get a 403 anyway.
  protected readonly isSuperAdmin = computed(() => this.authService.hasRole('SuperAdmin'));

  // ─── Computed: this user's true effective permission ids ─────────────────
  // Mirrors the backend's LocalAuthService.GetPermissionCodesAsync merge: role-derived permissions,
  // minus anything the user has a direct override for, plus back in only the enabled overrides.
  // Effective Permissions must reflect this, not just role.permissions — otherwise it goes stale
  // the moment an admin sets a direct override on the "Direct Permission Overrides" tab.
  effectivePermissionIds = computed<Set<string>>(() => {
    const user = this.user();
    if (!user) return new Set();

    const ids = new Set(user.permissions.map(p => p.id));
    for (const allocation of user.directPermissionAllocations) {
      ids.delete(allocation.permissionId);
      if (allocation.isEnabled) ids.add(allocation.permissionId);
    }
    return ids;
  });

  // ─── Computed: full catalog, each permission marked allowed/denied for this user ─
  catalogWithStatus = computed<StatusCategory[]>(() => {
    const allowedIds = this.effectivePermissionIds();
    return this.catalog().map(cat => ({
      id:          cat.id,
      displayName: cat.displayName,
      groups: cat.groups.map(g => ({
        id:          g.id,
        displayName: g.displayName,
        permissions: g.permissions.map(p => ({ ...p, allowed: allowedIds.has(p.id) })),
      })),
    }));
  });

  totalPermissionsCount = computed(() =>
    this.catalog().reduce((sum, cat) => sum + cat.groups.reduce((s, g) => s + g.permissions.length, 0), 0)
  );
  // Counted from catalogWithStatus (catalog permissions only) rather than user().permissions.length
  // directly — a role can carry permissions the catalog excludes (deactivated/hidden ones), which
  // would otherwise make allowed + denied not add up to the catalog total.
  allowedCount = computed(() =>
    this.catalogWithStatus().reduce(
      (sum, cat) => sum + cat.groups.reduce((s, g) => s + g.permissions.filter(p => p.allowed).length, 0), 0)
  );
  deniedCount  = computed(() => this.totalPermissionsCount() - this.allowedCount());

  // ─── Lifecycle ────────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.roleService.getPermissionCatalog()
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: catalog => this.catalog.set(catalog),
        error: () => this.toast.error('Failed to load the permission catalog.'),
      });

    if (this.id) {
      this.loadUser(this.id);
    }
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  // Delegates to the Direct-Permission-Overrides tab — the only editable, unsaved state this
  // route can carry (every other edit here goes through a MatDialog, which is closed/cancelled
  // independently of router navigation).
  hasUnsavedChanges(): boolean {
    return this.overridesComponent?.hasUnsavedChanges() ?? false;
  }

  isSaveInProgress(): boolean {
    return this.overridesComponent?.isSaveInProgress() ?? false;
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
          this.toast.error(err?.message ?? 'Failed to load user.');
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
      width: '820px',
      maxWidth: '95vw',
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

    if (action !== 'enable' && this.isSelf()) {
      this.toast.error(`You cannot ${action} your own account.`);
      return;
    }

    const call$ =
      action === 'enable'  ? this.userService.enableUser(u.id)  :
      action === 'disable' ? this.userService.disableUser(u.id) :
                             this.userService.suspendUser(u.id);

    call$.pipe(takeUntil(this.destroy$)).subscribe({
      next: updated => {
        this.user.set(updated);
        const label = action === 'enable' ? 'enabled' : action === 'disable' ? 'disabled' : 'suspended';
        this.toast.success(`User "${u.fullName}" ${label}.`);
      },
      error: err => {
        this.toast.error(err?.message ?? `Failed to ${action} user.`);
      },
    });
  }

  resetPassword(): void {
    const u = this.user();
    if (!u) return;
    this.userService.resetUserPassword(u.id, u.email)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (res) => {
          this.dialog.open(ResetPasswordLinkDialogComponent, {
            width: '540px', restoreFocus: false, data: res,
          });
        },
        error: (err: {message?: string}) => {
          this.toast.error(err?.message ?? 'Failed to reset password.');
        },
      });
  }

  // ─── Disable MFA (admin account-recovery override) ─────────────────────────
  disableMfa(): void {
    const u = this.user();
    if (!u) return;

    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '400px', restoreFocus: false,
      data: {
        title:        'Disable two-factor authentication',
        message:      `This removes 2FA from "${u.fullName}"'s account without requiring a code — ` +
                       'use this only for account recovery when they\'ve lost their authenticator and backup codes. ' +
                       'They can re-enroll from their own Security settings afterward.',
        confirmLabel: 'Disable 2FA',
        danger:       true,
      },
    });

    ref.afterClosed().subscribe(confirmed => {
      if (!confirmed) return;
      this.userService.disableUserMfa(u.id)
        .pipe(takeUntil(this.destroy$))
        .subscribe({
          next: (updated) => {
            this.user.set(updated);
            this.toast.success(`Two-factor authentication disabled for "${u.fullName}".`);
          },
          error: (err: {message?: string}) => {
            this.toast.error(err?.message ?? 'Failed to disable two-factor authentication.');
          },
        });
    });
  }

  // ─── Require / stop requiring MFA (admin policy toggle) ────────────────────
  toggleMfaRequirement(): void {
    const u = this.user();
    if (!u) return;
    const required = !u.mfaRequired;

    this.userService.setUserMfaRequirement(u.id, required)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: (updated) => {
          this.user.set(updated);
          this.toast.success(
            required
              ? `Two-factor authentication is now required for "${u.fullName}". They'll be prompted to set it up on next login.`
              : `Two-factor authentication is no longer required for "${u.fullName}".`,
          );
        },
        error: (err: {message?: string}) => {
          this.toast.error(err?.message ?? 'Failed to update the MFA requirement.');
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
          this.toast.success(`Invitation resent to "${u.email}".`);
        },
        error: (err: {message?: string}) => {
          this.toast.error(err?.message ?? 'Failed to resend invitation.');
        },
      });
  }

  deleteUser(): void {
    const u = this.user();
    if (!u) return;
    if (this.isSelf()) {
      this.toast.error('You cannot delete your own account.');
      return;
    }
    const confirmed = window.confirm(
      `Are you sure you want to delete "${u.fullName}"? This action cannot be undone.`,
    );
    if (!confirmed) return;

    this.userService.deleteUser(u.id)
      .pipe(takeUntil(this.destroy$))
      .subscribe({
        next: () => {
          this.toast.success(`User "${u.fullName}" deleted.`);
          this.router.navigate(['/user-management']);
        },
        error: (err: {message?: string}) => {
          this.toast.error(err?.message ?? 'Failed to delete user.');
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

  getStatusLabel(status: string): string {
    const map: Record<string, string> = {
      active:    'Active',
      inactive:  'Deactivated',
      suspended: 'Suspended',
      pending:   'Invited',
    };
    return map[status] ?? status;
  }
}
