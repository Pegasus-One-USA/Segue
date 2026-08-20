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
import { computeEffectiveCodes, toggleExplicitCode } from './permission-matrix-dependencies';
import { AuthService } from '../../../auth/services/auth.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';

// A resolved permission plus, where the static config set one, a tooltip that should replace the
// catalog's own perm.description on this specific checkbox — see MatrixAction.tooltipOverride.
interface GridCell {
  perm: Permission;
  tooltipOverride?: string;
}

// One row of a section's table, resolved against the live catalog — `cells` holds only the
// actions that actually resolved to a real Permission (an action whose code doesn't exist in this
// deployment's catalog yet renders no checkbox at all, rather than a broken one).
interface GridRow {
  id: string;
  label: string;
  superAdminOnly: boolean;
  note?: string;
  cells: Map<string, GridCell>;
  permissionIds: string[];
}

interface GridSection {
  id: string;
  label: string;
  note?: string;
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
    HideWithoutPermissionDirective,
  ],
  templateUrl: './role-permissions.component.html',
  styleUrls: ['./role-permissions.component.scss'],
})
export class RolePermissionsComponent implements OnChanges, HasUnsavedChanges {
  // Bound from the `:id` route segment via withComponentInputBinding().
  @Input() id!: string;

  private readonly svc         = inject(IRoleService);
  private readonly router      = inject(Router);
  private readonly toast       = inject(ToastService);
  private readonly authService = inject(AuthService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  // ─── Permission gating ──────────────────────────────────────────────────────
  // The route (`user-management.routes.ts`) requires only role.view to open this screen at all — a
  // view-only role can see the current grants, but every mutation (checkbox toggle, row/global "All",
  // Save) additionally requires role.edit, checked here since the route guard can't distinguish
  // "viewing" from "editing" within the same URL.
  protected readonly PermissionGroup = PermissionGroup;
  protected readonly PermissionAction = PermissionAction;
  protected readonly permissionCode = permissionCode;

  canEditPermissions(): boolean {
    return this.authService.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.Role, PermissionAction.Edit));
  }

  constructor() {
    this.unsavedChangesRegistry.register(() => this.hasUnsavedChanges() || this.isSaveInProgress());
  }

  readonly loading      = signal(true);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly roles        = signal<Role[]>([]);
  readonly role         = signal<Role | null>(null);
  readonly catalog      = signal<PermissionCategory[]>([]);
  readonly searchQuery  = signal('');

  // ─── Two-layer permission model ──────────────────────────────────────────────
  // EXPLICIT — codes (not ids) the admin has directly toggled on that exact row/action, scoped to
  // what this matrix manages (ALL_MATRIX_CODES). The ONLY thing togglePermission/toggleRow/toggleAll
  // mutate. Loading a role treats its full stored permission set as the new explicit baseline (the
  // database has no column to record "this one was only ever implied" — see
  // permission-matrix-dependencies.ts's module doc comment for why that's fine: effective state
  // still round-trips exactly through a save+reload even though the explicit/implied distinction
  // itself doesn't survive one).
  readonly explicitCodes = signal<Set<string>>(new Set());

  // EFFECTIVE — explicit ∪ transitive closure of required parents (permission-matrix-dependencies.ts).
  // Never its own signal; always derived, so it can never drift out of sync with explicitCodes.
  private readonly effectiveCodes = computed<Set<string>>(() => computeEffectiveCodes(this.explicitCodes()));

  // Permission ids for whatever this role holds OUTSIDE this matrix's scope (see `allPermissions`'s
  // own scoping comment) — captured once per load/save, never mutated by this screen, passed through
  // on the next save untouched.
  private outOfScopeIds = new Set<string>();

  // The full id set this screen displays as checked and would save — effectiveCodes translated to
  // ids, plus whatever's outside this matrix's scope. Every existing display method (isChecked,
  // isRowChecked, isAllChecked, ...) and save() read this exactly as before; none of them needed to
  // change for the two-layer model, because they never cared how the set was produced.
  readonly selectedIds = computed<Set<string>>(() => {
    const byCode = this.permissionByCode();
    const ids = new Set(this.outOfScopeIds);
    for (const code of this.effectiveCodes()) {
      const perm = byCode.get(code);
      if (perm) ids.add(perm.id);
    }
    return ids;
  });

  // Sections are expanded by default; ids in this set render collapsed (header only).
  readonly collapsedSectionIds = signal<Set<string>>(new Set());

  // Snapshot of explicitCodes as last loaded/saved from the DB — Reset restores this.
  private originalExplicitCodes = new Set<string>();

  readonly isSystemRole = computed(() => !!this.role()?.isSystemRole);

  private readonly allPermissionsFlat = computed<Permission[]>(() =>
    this.catalog().flatMap(cat => cat.groups.flatMap(g => g.permissions))
  );

  private readonly permissionByCode = computed(() => {
    const map = new Map<string, Permission>();
    for (const p of this.allPermissionsFlat()) map.set(p.name, p);
    return map;
  });

  // id -> code, the reverse of permissionByCode — every checkbox in the template only ever hands
  // toggle methods a permission id (see GridCell), but the dependency engine below reasons in codes.
  private readonly codeById = computed(() => {
    const map = new Map<string, string>();
    for (const p of this.allPermissionsFlat()) map.set(p.id, p.name);
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
        const cells = new Map<string, GridCell>();
        for (const action of row.actions ?? []) {
          const perm = byCode.get(action.code);
          if (perm) cells.set(action.label, { perm, tooltipOverride: action.tooltipOverride });
        }
        return {
          id: row.id,
          label: row.label,
          superAdminOnly: !!row.superAdminOnly,
          note: row.note,
          cells,
          permissionIds: [...cells.values()].map(c => c.perm.id),
        };
      });

      const actions = new Set<string>();
      for (const row of rows) for (const action of row.cells.keys()) actions.add(action);

      return {
        id: section.id,
        label: section.label,
        note: section.note,
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
              return [...row.cells.values()].some(c =>
                c.perm.displayName.toLowerCase().includes(q) || (c.perm.description ?? '').toLowerCase().includes(q)
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
        this.applyLoadedPermissions(current?.permissions ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set('Failed to load role permissions.');
      },
    });
  }

  // Splits a role's full stored permission list into this screen's two tracked pieces: the
  // explicit codes this matrix manages (the new explicit baseline — see explicitCodes's own doc
  // comment for why "everything stored" is treated as "everything explicit" on every load), and the
  // ids for anything outside this matrix's scope (passed through untouched on the next save). Shared
  // by loadData and save()'s success handler so both stay in sync with exactly the same rule.
  private applyLoadedPermissions(permissions: Permission[]): void {
    const explicit = new Set<string>();
    const outOfScope = new Set<string>();
    for (const p of permissions) {
      if (ALL_MATRIX_CODES.includes(p.name)) explicit.add(p.name); else outOfScope.add(p.id);
    }
    this.outOfScopeIds = outOfScope;
    this.explicitCodes.set(explicit);
    this.originalExplicitCodes = new Set(explicit);
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

  // ─── Single checkbox ──────────────────────────────────────────────────────
  isChecked(permissionId: string): boolean {
    return this.selectedIds().has(permissionId);
  }

  togglePermission(permissionId: string): void {
    if (this.isSystemRole() || !this.canEditPermissions()) return;
    const code = this.codeById().get(permissionId);
    if (!code) return;
    const checked = !this.selectedIds().has(permissionId);
    this.explicitCodes.set(toggleExplicitCode(this.explicitCodes(), code, checked, ALL_MATRIX_CODES));
  }

  // ─── Dependency engine bridge ───────────────────────────────────────────────
  // Node actions (epic.edit, sqlserver.create, ...) imply the matching Workflow-module action plus
  // workflow.view — see permission-matrix-dependencies.ts for the exact rule and why it's a UI/save
  // consistency concern only, never a substitute for the backend's own independent authorization.
  // toggleExplicitCode mutates only explicitCodes (in code-space); effectiveCodes/selectedIds are
  // always re-derived from it, so nothing here ever writes a permission id directly.

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
    if (this.isSystemRole() || !this.canEditPermissions()) return;
    // Routed through toggleExplicitCode per code (not a raw bulk add/clear) so this stays consistent
    // with the dependency engine — in practice a select-all/none across every code in scope always
    // ends in the same end state regardless (every parent is either already included, or already
    // being cleared alongside everything else), but going through one primitive avoids maintaining a
    // second, subtly-different mutation path.
    let next = this.explicitCodes();
    for (const code of ALL_MATRIX_CODES) {
      next = toggleExplicitCode(next, code, checked, ALL_MATRIX_CODES);
    }
    this.explicitCodes.set(next);
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
    if (this.isSystemRole() || !this.canEditPermissions()) return;
    // Per-code, not a raw bulk add/clear — this is what makes row-level "ALL" correctly cascade too:
    // checking Epic's ALL runs each of Epic's 5 actions through toggleExplicitCode, which is what
    // pulls in the matching Workflow action for every one of them (see permission-matrix-dependencies.ts).
    let next = this.explicitCodes();
    for (const cell of row.cells.values()) {
      next = toggleExplicitCode(next, cell.perm.name, checked, ALL_MATRIX_CODES);
    }
    this.explicitCodes.set(next);
  }

  // ─── Reset — discard local edits back to the last-loaded/saved DB state ──
  isDirty(): boolean {
    const current = this.explicitCodes();
    if (current.size !== this.originalExplicitCodes.size) return true;
    for (const code of current) {
      if (!this.originalExplicitCodes.has(code)) return true;
    }
    return false;
  }

  reset(): void {
    this.explicitCodes.set(new Set(this.originalExplicitCodes));
  }

  save(): void {
    const role = this.role();
    if (!role || this.isSystemRole()) return;
    // The Save button is hidden entirely without role.edit (see the template) — this re-check guards
    // against a permission change landing in another tab while this screen is still open, and against
    // direct invocation, not against a normally-reachable gap.
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.Role, PermissionAction.Edit), 'You do not have permission to modify role permissions.')) return;

    this.saving.set(true);
    this.errorMessage.set(null);
    // Effective ids, not explicit — the required parent permissions must actually be persisted in
    // PermissionAllocations, since the database has nowhere to record "this one is only implied."
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
        this.applyLoadedPermissions(updated.permissions);
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
