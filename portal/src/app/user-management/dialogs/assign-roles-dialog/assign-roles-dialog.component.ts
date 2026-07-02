// user-management/dialogs/assign-roles-dialog/assign-roles-dialog.component.ts
import {
  Component, OnInit, signal, computed, inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';

import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IUserService } from '../../../auth/services/i-user.service';
import { User, Role, Permission, UserRole } from '../../../auth/models/user.model';
import { ROLE_CONFIG } from '../../pages/user-list/user-list.component';

interface DialogData {
  user: User;
}

@Component({
  selector: 'app-assign-roles-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule,
    MatProgressSpinnerModule,
    MatDividerModule,
    MatChipsModule,
    MatTooltipModule,
  ],
  templateUrl: './assign-roles-dialog.component.html',
  styleUrls: ['./assign-roles-dialog.component.scss'],
})
export class AssignRolesDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  private readonly dialogRef   = inject(MatDialogRef<AssignRolesDialogComponent>);
  private readonly snackBar    = inject(MatSnackBar);
  readonly data                = inject<DialogData>(MAT_DIALOG_DATA);

  // ─── State signals ────────────────────────────────────────────────────────
  allRoles     = signal<Role[]>([]);
  loading      = signal(false);
  actionLoading = signal<string | null>(null); // roleId being toggled

  // Mutable local copy of user so we can reflect changes immediately
  localUser = signal<User>({ ...this.data.user, roles: [...this.data.user.roles] });

  readonly roleConfig = ROLE_CONFIG;

  // ─── Computed: assigned role IDs ─────────────────────────────────────────
  assignedRoleIds = computed(() =>
    new Set(this.localUser().roles.map(r => r.id))
  );

  // ─── Computed: effective permissions from assigned roles ─────────────────
  effectivePermissions = computed<Permission[]>(() => {
    const seen = new Set<string>();
    const perms: Permission[] = [];
    for (const role of this.localUser().roles) {
      for (const perm of role.permissions ?? []) {
        if (!seen.has(perm.id)) {
          seen.add(perm.id);
          perms.push(perm);
        }
      }
    }
    return perms;
  });

  // ─── Computed: grouped permissions ───────────────────────────────────────
  permissionGroups = computed<{ resource: string; permissions: Permission[] }[]>(() => {
    const groups = new Map<string, Permission[]>();
    for (const p of this.effectivePermissions()) {
      const list = groups.get(p.resource) ?? [];
      list.push(p);
      groups.set(p.resource, list);
    }
    return Array.from(groups.entries()).map(([resource, permissions]) => ({ resource, permissions }));
  });

  // ─── Lifecycle ────────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.loadRoles();
  }

  loadRoles(): void {
    this.loading.set(true);
    this.userService.getRoles().subscribe({
      next: roles => {
        this.allRoles.set(roles);
        this.loading.set(false);
      },
      error: err => {
        this.loading.set(false);
        this.snackBar.open(err?.message ?? 'Failed to load roles.', 'Dismiss', { duration: 4000 });
      },
    });
  }

  // ─── Role assignment ──────────────────────────────────────────────────────
  isAssigned(roleId: string): boolean {
    return this.assignedRoleIds().has(roleId);
  }

  toggleRole(role: Role): void {
    if (this.actionLoading()) return; // prevent concurrent toggles

    if (this.isAssigned(role.id)) {
      this.removeRole(role);
    } else {
      this.assignRole(role);
    }
  }

  assignRole(role: Role): void {
    this.actionLoading.set(role.id);
    this.userService.assignRole(this.localUser().id, role.id).subscribe({
      next: updatedUser => {
        this.localUser.set(updatedUser);
        this.actionLoading.set(null);
        this.snackBar.open(
          `Role "${role.displayName}" assigned.`,
          'Dismiss',
          { duration: 2500 },
        );
      },
      error: err => {
        this.actionLoading.set(null);
        this.snackBar.open(
          err?.message ?? `Failed to assign role "${role.displayName}".`,
          'Dismiss',
          { duration: 4000 },
        );
      },
    });
  }

  removeRole(role: Role): void {
    this.actionLoading.set(role.id);
    this.userService.removeRole(this.localUser().id, role.id).subscribe({
      next: updatedUser => {
        this.localUser.set(updatedUser);
        this.actionLoading.set(null);
        this.snackBar.open(
          `Role "${role.displayName}" removed.`,
          'Dismiss',
          { duration: 2500 },
        );
      },
      error: err => {
        this.actionLoading.set(null);
        this.snackBar.open(
          err?.message ?? `Failed to remove role "${role.displayName}".`,
          'Dismiss',
          { duration: 4000 },
        );
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
