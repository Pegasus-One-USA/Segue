// user-management/dialogs/assign-roles-dialog/assign-roles-dialog.component.ts
import {
  Component, OnInit, signal, computed, inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { forkJoin, of } from 'rxjs';
import { switchMap } from 'rxjs/operators';

import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule, MatRadioChange } from '@angular/material/radio';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ToastService } from '../../../services/toast.service';
import { IUserService } from '../../../auth/services/i-user.service';
import { IRoleService } from '../../services/i-role.service';
import { User, Role, Permission, PermissionCategory, UserRole } from '../../../auth/models/user.model';
import { ROLE_CONFIG } from '../../pages/user-list/user-list.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';

function backendErrorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: { error?: string } } | null)?.error;
  return body?.error ?? fallback;
}

interface DialogData {
  user: User;
}

// A permission within the allowed/denied preview — same shape as `Permission` plus whether the
// currently-selected role grants it.
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
  selector: 'app-assign-roles-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatRadioModule,
    MatProgressSpinnerModule,
    MatDividerModule,
    MatTooltipModule,
  ],
  templateUrl: './assign-roles-dialog.component.html',
  styleUrls: ['./assign-roles-dialog.component.scss'],
})
export class AssignRolesDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  private readonly roleService = inject(IRoleService);
  private readonly authStore   = inject(AuthStore);
  private readonly dialogRef   = inject(MatDialogRef<AssignRolesDialogComponent>);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly data                = inject<DialogData>(MAT_DIALOG_DATA);

  // ─── State signals ────────────────────────────────────────────────────────
  allRoles      = signal<Role[]>([]);
  catalog       = signal<PermissionCategory[]>([]);
  loading       = signal(false);
  actionLoading = signal<string | null>(null); // roleId being assigned

  // Mutable local copy of user so we can reflect changes immediately
  localUser = signal<User>({ ...this.data.user, roles: [...this.data.user.roles] });

  readonly roleConfig = ROLE_CONFIG;

  // ─── Computed: the single assigned role (a user has at most one) ─────────
  assignedRole = computed<Role | null>(() => this.localUser().roles[0] ?? null);

  assignedRoleIds = computed(() =>
    new Set(this.localUser().roles.map(r => r.id))
  );

  // The backend refuses to remove your own Super Admin role (self-lockout protection) — this
  // dialog is editing the currently logged-in user's own account and their role is Super Admin,
  // so every other role must stay disabled rather than let the click round-trip into a 400.
  isEditingOwnSuperAdmin = computed(() =>
    this.assignedRole()?.name === 'SuperAdmin' && this.data.user.id === this.authStore.currentUser()?.id
  );

  // ─── Computed: what the assigned role grants ──────────────────────────────
  effectivePermissions = computed<Permission[]>(() => this.assignedRole()?.permissions ?? []);

  // ─── Computed: full catalog, each permission marked allowed/denied by the assigned role ─
  catalogWithStatus = computed<StatusCategory[]>(() => {
    const allowedIds = new Set(this.effectivePermissions().map(p => p.id));
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
  // Counted from catalogWithStatus (catalog permissions only) rather than effectivePermissions()
  // directly — a role can carry permissions the catalog excludes (deactivated/hidden ones), which
  // would otherwise make allowed + denied not add up to the catalog total.
  allowedCount = computed(() =>
    this.catalogWithStatus().reduce(
      (sum, cat) => sum + cat.groups.reduce((s, g) => s + g.permissions.filter(p => p.allowed).length, 0), 0)
  );
  deniedCount  = computed(() => this.totalPermissionsCount() - this.allowedCount());

  // ─── Lifecycle ────────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.loadRoles();
  }

  loadRoles(): void {
    this.loading.set(true);
    forkJoin({
      roles:   this.userService.getRoles(),
      catalog: this.roleService.getPermissionCatalog(),
    }).subscribe({
      next: ({ roles, catalog }) => {
        this.allRoles.set(roles);
        this.catalog.set(catalog);
        this.loading.set(false);
      },
      error: err => {
        this.loading.set(false);
        this.toast.error(backendErrorMessage(err, 'Failed to load roles.'));
      },
    });
  }

  // ─── Role assignment — a user may only have one role at a time ───────────
  isAssigned(roleId: string): boolean {
    return this.assignedRoleIds().has(roleId);
  }

  // Every role except the one already assigned is unselectable while editing your own
  // Super Admin account — see isEditingOwnSuperAdmin.
  isRoleDisabled(roleId: string): boolean {
    return this.isEditingOwnSuperAdmin() && !this.isAssigned(roleId);
  }

  onRoleRadioChange(change: MatRadioChange): void {
    const role = this.allRoles().find(r => r.id === change.value);
    if (role) this.selectRole(role);
  }

  selectRole(role: Role): void {
    if (this.actionLoading() || this.isRoleDisabled(role.id)) return;
    // Each click here is its own immediate mutation (no separate Save/confirm step), so the
    // permission re-check belongs on every call, not just at dialog-open — this is the sole
    // enforcement point shared by both of this dialog's callers (user-list, user-detail).
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Assign), 'You do not have permission to assign roles.')) return;

    const currentRoles = this.localUser().roles;
    const alreadyAssigned = currentRoles.some(r => r.id === role.id);
    if (alreadyAssigned && currentRoles.length === 1) return; // already the sole role

    this.actionLoading.set(role.id);
    const rolesToRemove = currentRoles.filter(r => r.id !== role.id);

    const removeChain = rolesToRemove.reduce(
      (chain, r) => chain.pipe(switchMap(() => this.userService.removeRole(this.localUser().id, r.id))),
      of(this.localUser()),
    );

    // The target role may already be one of several assigned roles (legacy multi-role state) — in
    // that case just remove the others, don't re-assign a role the user already has.
    const finalChain = alreadyAssigned
      ? removeChain
      : removeChain.pipe(switchMap(() => this.userService.assignRole(this.localUser().id, role.id)));

    finalChain.subscribe({
      next: updatedUser => {
        this.localUser.set(updatedUser);
        this.actionLoading.set(null);
        this.toast.success(`Role set to "${role.displayName}".`);
      },
      error: err => {
        this.actionLoading.set(null);
        this.toast.error(backendErrorMessage(err, `Failed to set role "${role.displayName}".`));
      },
    });
  }

  // ─── Dialog close ─────────────────────────────────────────────────────────
  close(): void {
    this.dialogRef.close(this.localUser());
  }

  getRoleConfig(role: UserRole) {
    return ROLE_CONFIG[role] ?? { label: role, color: '#64748B', bg: '#F1F5F9' };
  }
}
