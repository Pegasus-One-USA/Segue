// user-management/pages/role-permissions/role-permissions.component.ts
import { Component, OnChanges, SimpleChanges, Input, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { forkJoin } from 'rxjs';

import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IRoleService } from '../../services/i-role.service';
import { Role, Permission } from '../../../auth/models/user.model';

interface PermissionGroup {
  resource: string;
  permissions: Permission[];
}

@Component({
  selector: 'app-role-permissions',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule,
    MatIconModule,
    MatSelectModule,
    MatFormFieldModule,
    MatCheckboxModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './role-permissions.component.html',
  styleUrls: ['./role-permissions.component.scss'],
})
export class RolePermissionsComponent implements OnChanges {
  // Bound from the `:id` route segment via withComponentInputBinding().
  @Input() id!: string;

  private readonly svc    = inject(IRoleService);
  private readonly router = inject(Router);
  private readonly snack  = inject(MatSnackBar);

  readonly loading      = signal(true);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly roles          = signal<Role[]>([]);
  readonly role           = signal<Role | null>(null);
  readonly allPermissions = signal<Permission[]>([]);
  readonly selectedIds    = signal<Set<string>>(new Set());
  readonly searchQuery    = signal('');

  // Snapshot of the role's permissions as last loaded/saved from the DB — Reset restores this.
  private originalSelectedIds = new Set<string>();

  readonly isSystemRole = computed(() => !!this.role()?.isSystemRole);

  private readonly groupedPermissions = computed<PermissionGroup[]>(() => {
    const groups = new Map<string, Permission[]>();
    for (const p of this.allPermissions()) {
      const key = p.resource || 'Other';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(p);
    }
    return [...groups.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([resource, permissions]) => ({ resource, permissions }));
  });

  // Matches the search string against the group name or any permission's name/description.
  // A group-name match keeps every permission in that group; otherwise only matching permissions show.
  readonly permissionGroups = computed<PermissionGroup[]>(() => {
    const all = this.groupedPermissions();
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return all;

    return all
      .map(group => {
        const groupMatches = group.resource.toLowerCase().includes(q);
        const permissions = groupMatches
          ? group.permissions
          : group.permissions.filter(p =>
              p.name.toLowerCase().includes(q) || (p.description ?? '').toLowerCase().includes(q)
            );
        return { resource: group.resource, permissions };
      })
      .filter(group => group.permissions.length > 0);
  });

  // Route param changes reuse this component instance, so ngOnInit won't refire — reload here.
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['id']) {
      this.loadData();
    }
  }

  private loadData(): void {
    this.loading.set(true);
    this.errorMessage.set(null);

    forkJoin({
      roles:       this.svc.getRoles(),
      permissions: this.svc.getPermissions(),
    }).subscribe({
      next: ({ roles, permissions }) => {
        this.roles.set(roles);
        this.allPermissions.set(permissions);

        const current = roles.find(r => r.id === this.id) ?? null;
        this.role.set(current);
        this.originalSelectedIds = new Set(current?.permissions.map(p => p.id) ?? []);
        this.selectedIds.set(new Set(this.originalSelectedIds));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set('Failed to load role permissions.');
      },
    });
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
  }

  onRoleChange(roleId: string): void {
    if (roleId === this.role()?.id) return;
    this.router.navigate(['/user-management/roles', roleId, 'permissions']);
  }

  isChecked(permissionId: string): boolean {
    return this.selectedIds().has(permissionId);
  }

  togglePermission(permissionId: string): void {
    if (this.isSystemRole()) return;
    const next = new Set(this.selectedIds());
    if (next.has(permissionId)) next.delete(permissionId); else next.add(permissionId);
    this.selectedIds.set(next);
  }

  // ─── Tri-state select-all — whole list ────────────────────────────────────
  isAllChecked(): boolean {
    const all = this.allPermissions();
    return all.length > 0 && all.every(p => this.selectedIds().has(p.id));
  }

  isAllIndeterminate(): boolean {
    return this.selectedIds().size > 0 && !this.isAllChecked();
  }

  toggleAll(checked: boolean): void {
    if (this.isSystemRole()) return;
    this.selectedIds.set(checked ? new Set(this.allPermissions().map(p => p.id)) : new Set());
  }

  // ─── Tri-state select-all — single group ─────────────────────────────────
  isGroupChecked(group: PermissionGroup): boolean {
    return group.permissions.length > 0 && group.permissions.every(p => this.selectedIds().has(p.id));
  }

  isGroupIndeterminate(group: PermissionGroup): boolean {
    const selected = group.permissions.filter(p => this.selectedIds().has(p.id)).length;
    return selected > 0 && selected < group.permissions.length;
  }

  toggleGroup(group: PermissionGroup, checked: boolean): void {
    if (this.isSystemRole()) return;
    const next = new Set(this.selectedIds());
    for (const p of group.permissions) {
      if (checked) next.add(p.id); else next.delete(p.id);
    }
    this.selectedIds.set(next);
  }

  // ─── Reset — discard local edits back to the last-loaded/saved DB state ──
  isDirty(): boolean {
    const current = this.selectedIds();
    if (current.size !== this.originalSelectedIds.size) return true;
    for (const id of current) {
      if (!this.originalSelectedIds.has(id)) return true;
    }
    return false;
  }

  reset(): void {
    this.selectedIds.set(new Set(this.originalSelectedIds));
  }

  save(): void {
    const role = this.role();
    if (!role || this.isSystemRole()) return;

    this.saving.set(true);
    this.errorMessage.set(null);
    const permissionIds = [...this.selectedIds()];

    // updateRole replaces the full permission set, so name/description are resent unchanged —
    // this screen owns permissions only, the role dialog owns name/description only.
    this.svc.updateRole(role.id, {
      name: role.displayName,
      description: role.description,
      permissionIds,
    }).subscribe({
      next: updated => {
        this.saving.set(false);
        this.role.set(updated);
        this.originalSelectedIds = new Set(updated.permissions.map(p => p.id));
        this.selectedIds.set(new Set(this.originalSelectedIds));
        this.roles.update(list => list.map(r => (r.id === updated.id ? updated : r)));
        this.snack.open(`Permissions updated for "${updated.displayName}".`, 'Dismiss', { duration: 3000 });
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to update permissions. Please try again.');
      },
    });
  }

  goBack(): void {
    this.router.navigate(['/user-management/roles']);
  }
}
