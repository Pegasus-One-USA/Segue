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

import { IRoleService } from '../../services/i-role.service';
import { Role, Permission, PermissionCategory } from '../../../auth/models/user.model';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';
import { ALL_MATRIX_CODES, MATRIX_SECTIONS, MatrixRow, MatrixSection } from './permission-matrix.config';

// One row of a section's table, resolved against the live catalog — `cells` holds only the
// actions that actually resolved to a real Permission (an action whose code doesn't exist in this
// deployment's catalog yet renders no checkbox at all, rather than a broken one).
interface GridRow {
  id: string;
  label: string;
  superAdminOnly: boolean;
  note?: string;
  cells: Map<string, Permission>;
  permissionIds: string[];
}

interface GridSection {
  id: string;
  label: string;
  actions: string[];
  rows: GridRow[];
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
  private readonly toast  = inject(ToastService);
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

  // Sections are expanded by default; ids in this set render collapsed (header only).
  readonly collapsedSectionIds = signal<Set<string>>(new Set());

  // Snapshot of the role's permissions as last loaded/saved from the DB — Reset restores this.
  private originalSelectedIds = new Set<string>();

  readonly isSystemRole = computed(() => !!this.role()?.isSystemRole);

  private readonly allPermissionsFlat = computed<Permission[]>(() =>
    this.catalog().flatMap(cat => cat.groups.flatMap(g => g.permissions))
  );

  private readonly permissionByCode = computed(() => {
    const map = new Map<string, Permission>();
    for (const p of this.allPermissionsFlat()) map.set(p.name, p);
    return map;
  });

  // Resolves the static MATRIX_SECTIONS layout against the live catalog — every action whose code
  // doesn't exist in this deployment yet is silently dropped from `cells` (defensive; every code
  // referenced here is a real, already-seeded/discovered permission as of this screen's last
  // verification, so this only matters for an out-of-sync environment).
  private readonly gridSections = computed<GridSection[]>(() => {
    const byCode = this.permissionByCode();

    return MATRIX_SECTIONS.map((section: MatrixSection) => {
      const rows: GridRow[] = section.rows.map((row: MatrixRow) => {
        const cells = new Map<string, Permission>();
        for (const action of row.actions ?? []) {
          const perm = byCode.get(action.code);
          if (perm) cells.set(action.label, perm);
        }
        return {
          id: row.id,
          label: row.label,
          superAdminOnly: !!row.superAdminOnly,
          note: row.note,
          cells,
          permissionIds: [...cells.values()].map(p => p.id),
        };
      });

      const actions = new Set<string>();
      for (const row of rows) for (const action of row.cells.keys()) actions.add(action);

      return {
        id: section.id,
        label: section.label,
        actions: [...actions].sort((a, b) => a.localeCompare(b)),
        rows,
      };
    });
  });

  // Matches the search string against a section's display name or a row's/permission's display
  // name/description. A section-name match keeps every row beneath it; otherwise a row survives
  // only if it (or one of its own actions) matches.
  readonly permissionGrid = computed<GridSection[]>(() => {
    const all = this.gridSections();
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return all;

    return all
      .map(section => {
        const sectionMatches = section.label.toLowerCase().includes(q);
        const rows = sectionMatches
          ? section.rows
          : section.rows.filter(row => {
              if (row.label.toLowerCase().includes(q)) return true;
              return [...row.cells.values()].some(p =>
                p.displayName.toLowerCase().includes(q) || (p.description ?? '').toLowerCase().includes(q)
              );
            });
        return { ...section, rows };
      })
      .filter(section => section.rows.length > 0);
  });

  // Scoped to exactly what this matrix can manage (ALL_MATRIX_CODES), not the whole catalog — a
  // handful of existing permissions (Pipeline.Execute, Report.View, Payload.View, the unused
  // per-vendor Read/Assign/Execute actions on Epic/Athenahealth/Cerner) have no menu home and
  // aren't shown here; this screen never adds or removes them, so a role's existing grant of any
  // of those passes through save untouched.
  readonly allPermissions = computed<Permission[]>(() => {
    const byCode = this.permissionByCode();
    return ALL_MATRIX_CODES.map(code => byCode.get(code)).filter((p): p is Permission => !!p);
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

  isSectionCollapsed(sectionId: string): boolean {
    return this.collapsedSectionIds().has(sectionId);
  }

  toggleSectionCollapsed(sectionId: string): void {
    const next = new Set(this.collapsedSectionIds());
    if (next.has(sectionId)) next.delete(sectionId); else next.add(sectionId);
    this.collapsedSectionIds.set(next);
  }

  // ─── Single checkbox — every action controls exactly one permission id, independently. No
  // dependency/cascade of any kind: checking or unchecking one permission never touches another. ─
  isChecked(permissionId: string): boolean {
    return this.selectedIds().has(permissionId);
  }

  togglePermission(permissionId: string): void {
    if (this.isSystemRole()) return;
    const next = new Set(this.selectedIds());
    if (next.has(permissionId)) next.delete(permissionId); else next.add(permissionId);
    this.selectedIds.set(next);
  }

  // ─── Tri-state select-all — whole matrix ──────────────────────────────────
  isAllChecked(): boolean {
    const all = this.allPermissions();
    return all.length > 0 && all.every(p => this.selectedIds().has(p.id));
  }

  isAllIndeterminate(): boolean {
    const all = new Set(this.allPermissions().map(p => p.id));
    const selectedInMatrix = [...this.selectedIds()].filter(id => all.has(id)).length;
    return selectedInMatrix > 0 && selectedInMatrix < all.size;
  }

  toggleAll(checked: boolean): void {
    if (this.isSystemRole()) return;
    const matrixIds = new Set(this.allPermissions().map(p => p.id));
    if (checked) {
      const next = new Set(this.selectedIds());
      for (const id of matrixIds) next.add(id);
      this.selectedIds.set(next);
    } else {
      // Only clear what this matrix manages — anything outside its scope (see `allPermissions`)
      // is left exactly as-is, never silently dropped by a "select none" click.
      this.selectedIds.set(new Set([...this.selectedIds()].filter(id => !matrixIds.has(id))));
    }
  }

  // ─── Tri-state select-all — single row ────────────────────────────────────
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
        this.toast.success(`Permissions updated for "${updated.displayName}".`);
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
