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
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IRoleService } from '../../services/i-role.service';
import { Role, Permission, PermissionCategory } from '../../../auth/models/user.model';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';

// One row of a category table — a Permission Group. `cells` holds only the actions that actually
// have a Permission for this group (capitalized action label -> Permission); an action with no
// entry here renders as a blank cell, never a checkbox.
interface GridRow {
  id:            string;
  name:          string;
  displayName:   string;
  cells:         Map<string, Permission>;
  permissionIds: string[];
}

// One table — a Permission Category. `actions` is the sorted union of action labels across this
// category's rows, i.e. the table's columns; different categories can and do have different columns.
interface GridCategory {
  id:          string;
  name:        string;
  displayName: string;
  actions:     string[];
  rows:        GridRow[];
}

function capitalize(value: string): string {
  return value ? value.charAt(0).toUpperCase() + value.slice(1) : value;
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
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './role-permissions.component.html',
  styleUrls: ['./role-permissions.component.scss'],
})
export class RolePermissionsComponent implements OnChanges, HasUnsavedChanges {
  // Bound from the `:id` route segment via withComponentInputBinding().
  @Input() id!: string;

  private readonly svc    = inject(IRoleService);
  private readonly router = inject(Router);
  private readonly snack  = inject(MatSnackBar);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  constructor() {
    this.unsavedChangesRegistry.register(() => this.hasUnsavedChanges() || this.isSaveInProgress());
  }

  readonly loading      = signal(true);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly roles        = signal<Role[]>([]);
  readonly role         = signal<Role | null>(null);
  readonly catalog      = signal<PermissionCategory[]>([]);
  readonly selectedIds  = signal<Set<string>>(new Set());
  readonly searchQuery  = signal('');

  // Category tables are expanded by default; ids in this set render collapsed (header only).
  readonly collapsedCategoryIds = signal<Set<string>>(new Set());

  // Snapshot of the role's permissions as last loaded/saved from the DB — Reset restores this.
  private originalSelectedIds = new Set<string>();

  readonly isSystemRole = computed(() => !!this.role()?.isSystemRole);

  readonly allPermissions = computed<Permission[]>(() =>
    this.catalog().flatMap(cat => cat.groups.flatMap(g => g.permissions))
  );

  private readonly gridCategories = computed<GridCategory[]>(() => {
    return this.catalog().map(cat => {
      const rows: GridRow[] = cat.groups.map(g => {
        const cells = new Map<string, Permission>();
        for (const p of g.permissions) {
          cells.set(capitalize(p.action), p);
        }
        return {
          id:            g.id,
          name:          g.name,
          displayName:   g.displayName,
          cells,
          permissionIds: g.permissions.map(p => p.id),
        };
      });

      const actions = new Set<string>();
      for (const row of rows) for (const action of row.cells.keys()) actions.add(action);

      return {
        id:          cat.id,
        name:        cat.name,
        displayName: cat.displayName,
        actions:     [...actions].sort((a, b) => a.localeCompare(b)),
        rows,
      };
    });
  });

  // Matches the search string against a category's/group's display name or a permission's display
  // name/description. A category or group name match keeps every row/cell beneath it; otherwise a
  // row survives only if at least one of its permissions matches.
  readonly permissionGrid = computed<GridCategory[]>(() => {
    const all = this.gridCategories();
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return all;

    return all
      .map(cat => {
        const categoryMatches = cat.displayName.toLowerCase().includes(q);
        const rows = categoryMatches
          ? cat.rows
          : cat.rows.filter(row => {
              if (row.displayName.toLowerCase().includes(q)) return true;
              return [...row.cells.values()].some(p =>
                p.displayName.toLowerCase().includes(q) || (p.description ?? '').toLowerCase().includes(q)
              );
            });
        return { ...cat, rows };
      })
      .filter(cat => cat.rows.length > 0);
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
      roles:   this.svc.getRoles(),
      catalog: this.svc.getPermissionCatalog(),
    }).subscribe({
      next: ({ roles, catalog }) => {
        this.roles.set(roles);
        this.catalog.set(catalog);

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

  isCategoryCollapsed(categoryId: string): boolean {
    return this.collapsedCategoryIds().has(categoryId);
  }

  toggleCategoryCollapsed(categoryId: string): void {
    const next = new Set(this.collapsedCategoryIds());
    if (next.has(categoryId)) next.delete(categoryId); else next.add(categoryId);
    this.collapsedCategoryIds.set(next);
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

  // ─── Tri-state select-all — single row (Permission Group) ────────────────
  isRowChecked(row: GridRow): boolean {
    return row.permissionIds.length > 0 && row.permissionIds.every(id => this.selectedIds().has(id));
  }

  isRowIndeterminate(row: GridRow): boolean {
    const selected = row.permissionIds.filter(id => this.selectedIds().has(id)).length;
    return selected > 0 && selected < row.permissionIds.length;
  }

  toggleRow(row: GridRow, checked: boolean): void {
    if (this.isSystemRole()) return;
    const next = new Set(this.selectedIds());
    for (const id of row.permissionIds) {
      if (checked) next.add(id); else next.delete(id);
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

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    return this.isDirty();
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }
}
