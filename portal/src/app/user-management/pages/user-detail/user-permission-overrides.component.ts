// user-management/pages/user-detail/user-permission-overrides.component.ts
import {
  Component,
  OnChanges,
  SimpleChanges,
  Input,
  Output,
  EventEmitter,
  inject,
  signal,
  computed,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { forkJoin } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import {
  MatCheckboxModule,
  MatCheckboxChange,
} from '@angular/material/checkbox';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDialog } from '@angular/material/dialog';

import { IUserService } from '../../../auth/services/i-user.service';
import { IRoleService } from '../../services/i-role.service';
import {
  Permission,
  PermissionCategory,
  PermissionAllocationDto,
} from '../../../auth/models/user.model';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ConfirmDialogComponent } from '../../dialogs/confirm-dialog/confirm-dialog.component';
import {
  TriStateToggleComponent,
  TriState,
} from './tri-state-toggle.component';
import { ToastService } from '../../../services/toast.service';

// One row of a category table — a Permission Group. Mirrors role-permissions.component's grid:
// `cells` holds only the actions that actually have a Permission for this group; an action with
// no entry renders as a blank cell, never a toggle.
interface GridRow {
  id: string;
  name: string;
  displayName: string;
  cells: Map<string, Permission>;
  permissionIds: string[];
}

// One table — a Permission Category. `actions` is the sorted union of action labels across this
// category's rows, i.e. the table's columns; different categories can and do have different columns.
interface GridCategory {
  id: string;
  name: string;
  displayName: string;
  actions: string[];
  rows: GridRow[];
}

function capitalize(value: string): string {
  return value ? value.charAt(0).toUpperCase() + value.slice(1) : value;
}

@Component({
  selector: 'app-user-permission-overrides',
  standalone: true,
  imports: [
    CommonModule,
    MatIconModule,
    MatCheckboxModule,
    MatButtonModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    TriStateToggleComponent,
  ],
  templateUrl: './user-permission-overrides.component.html',
  styleUrls: ['./user-permission-overrides.component.scss'],
})
export class UserPermissionOverridesComponent
  implements OnChanges, HasUnsavedChanges
{
  // Bound from the parent user-detail component.
  @Input() userId!: string;
  @Input() userDisplayName = 'this user';
  // The user's role-derived permissions — used only to render the read-only "inherit from role"
  // view (what's actually granted via their role(s), with no direct overrides applied).
  @Input() rolePermissions: Permission[] = [];
  // Fired after overrides are actually persisted (Save, or the "Inherit from role" bulk clear) —
  // the parent's own Effective Permissions view is stale otherwise, since it isn't this component.
  @Output() overridesChanged = new EventEmitter<void>();

  private readonly userService = inject(IUserService);
  private readonly roleService = inject(IRoleService);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(ToastService);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly clearing = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly catalog = signal<PermissionCategory[]>([]);
  readonly searchQuery = signal('');

  // Pending, unsaved overrides — permissionId -> grant(true)/deny(false). Absence of a key means
  // "inherit from role". This is a local staging area: nothing here is persisted until Save.
  readonly overrides = signal<Map<string, boolean>>(new Map());

  // True when the user currently has no direct overrides at all — the grid renders read-only,
  // showing exactly what the role(s) grant. Toggling this off unlocks editing; toggling it on
  // (with a confirmation) deletes every direct override the user has.
  readonly inheritFromRole = signal(true);

  // Category tables are expanded by default; ids in this set render collapsed (header only).
  readonly collapsedCategoryIds = signal<Set<string>>(new Set());

  // Snapshot of the last loaded/saved overrides — Reset/Cancel restore this.
  private originalOverrides = new Map<string, boolean>();

  readonly overriddenCount = computed(() => this.overrides().size);

  private readonly gridCategories = computed<GridCategory[]>(() => {
    return this.catalog().map((cat) => {
      const rows: GridRow[] = cat.groups.map((g) => {
        const cells = new Map<string, Permission>();
        for (const p of g.permissions) {
          cells.set(capitalize(p.action), p);
        }
        return {
          id: g.id,
          name: g.name,
          displayName: g.displayName,
          cells,
          permissionIds: g.permissions.map((p) => p.id),
        };
      });

      const actions = new Set<string>();
      for (const row of rows)
        for (const action of row.cells.keys()) actions.add(action);

      return {
        id: cat.id,
        name: cat.name,
        displayName: cat.displayName,
        actions: [...actions].sort((a, b) => a.localeCompare(b)),
        rows,
      };
    });
  });

  // Matches the search string against a category's/group's display name or a permission's display
  // name/description — same semantics as the role-permissions grid.
  readonly permissionGrid = computed<GridCategory[]>(() => {
    const all = this.gridCategories();
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return all;

    return all
      .map((cat) => {
        const categoryMatches = cat.displayName.toLowerCase().includes(q);
        const rows = categoryMatches
          ? cat.rows
          : cat.rows.filter((row) => {
              if (row.displayName.toLowerCase().includes(q)) return true;
              return [...row.cells.values()].some(
                (p) =>
                  p.displayName.toLowerCase().includes(q) ||
                  (p.description ?? '').toLowerCase().includes(q),
              );
            });
        return { ...cat, rows };
      })
      .filter((cat) => cat.rows.length > 0);
  });

  // Route/tab changes reuse this component instance, so ngOnInit won't refire — reload here.
  ngOnChanges(changes: SimpleChanges): void {
    if (changes['userId'] && this.userId) {
      this.loadData();
    }
  }

  private loadData(): void {
    this.loading.set(true);
    this.errorMessage.set(null);

    forkJoin({
      catalog: this.roleService.getPermissionCatalog(),
      allocations: this.userService.getUserPermissionAllocations(this.userId),
    }).subscribe({
      next: ({ catalog, allocations }) => {
        this.catalog.set(catalog);
        this.applyLoadedOverrides(allocations);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set('Failed to load direct permission overrides.');
      },
    });
  }

  private applyLoadedOverrides(allocations: PermissionAllocationDto[]): void {
    const map = new Map<string, boolean>();
    for (const a of allocations) map.set(a.permissionId, a.isEnabled);
    this.originalOverrides = map;
    this.overrides.set(new Map(map));
    this.inheritFromRole.set(map.size === 0);
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
  }

  isCategoryCollapsed(categoryId: string): boolean {
    return this.collapsedCategoryIds().has(categoryId);
  }

  toggleCategoryCollapsed(categoryId: string): void {
    const next = new Set(this.collapsedCategoryIds());
    if (next.has(categoryId)) next.delete(categoryId);
    else next.add(categoryId);
    this.collapsedCategoryIds.set(next);
  }

  // ─── Per-cell override (staged, not yet saved) ─────────────────────────────
  // Individual permissions are a plain checked/unchecked grant — checked stages an explicit
  // grant override, unchecked clears any override (back to inherit). Explicit deny is only set
  // in bulk, via the row's tri-state "All" control below.
  stateFor(permissionId: string): TriState {
    if (this.inheritFromRole()) {
      return this.rolePermissions.some((p) => p.id === permissionId)
        ? 'grant'
        : 'inherit';
    }
    const value = this.overrides().get(permissionId);
    return value === undefined ? 'inherit' : value ? 'grant' : 'deny';
  }

  isCellGranted(permissionId: string): boolean {
    return this.stateFor(permissionId) === 'grant';
  }

  setCellGranted(permissionId: string, granted: boolean): void {
    const map = new Map(this.overrides());
    if (granted) map.set(permissionId, true);
    else map.delete(permissionId);
    this.overrides.set(map);
  }

  // ─── Per-row tri-state bulk override (staged, not yet saved) ─────────────
  // Shows the row's uniform state if every permission in it currently shares one; otherwise no
  // button is highlighted. Selecting a state applies it to every permission in the row.
  rowState(row: GridRow): TriState | null {
    const states = row.permissionIds.map((id) => this.stateFor(id));
    const first = states[0];
    return states.every((s) => s === first) ? first : null;
  }

  setRowState(row: GridRow, next: TriState): void {
    const map = new Map(this.overrides());
    for (const id of row.permissionIds) {
      if (next === 'inherit') map.delete(id);
      else map.set(id, next === 'grant');
    }
    this.overrides.set(map);
  }

  // ─── Global tri-state bulk override — every permission, regardless of search filter ──────
  private readonly allPermissionIds = computed<string[]>(() =>
    this.gridCategories().flatMap((cat) =>
      cat.rows.flatMap((row) => row.permissionIds),
    ),
  );

  globalState(): TriState | null {
    const ids = this.allPermissionIds();
    if (ids.length === 0) return 'inherit';
    const states = ids.map((id) => this.stateFor(id));
    const first = states[0];
    return states.every((s) => s === first) ? first : null;
  }

  setGlobalState(next: TriState): void {
    const map = new Map(this.overrides());
    for (const id of this.allPermissionIds()) {
      if (next === 'inherit') map.delete(id);
      else map.set(id, next === 'grant');
    }
    this.overrides.set(map);
  }

  // ─── Dirty tracking / Reset / Cancel / Save ───────────────────────────────
  isDirty(): boolean {
    const current = this.overrides();
    if (current.size !== this.originalOverrides.size) return true;
    for (const [id, value] of current) {
      if (this.originalOverrides.get(id) !== value) return true;
    }
    return false;
  }

  reset(): void {
    this.overrides.set(new Map(this.originalOverrides));
  }

  cancel(): void {
    this.reset();
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts, consulted via the parent user-detail route) ──
  hasUnsavedChanges(): boolean {
    return this.isDirty();
  }

  isSaveInProgress(): boolean {
    return this.saving() || this.clearing();
  }

  save(): void {
    this.saving.set(true);
    this.errorMessage.set(null);

    const permissionIdToIsEnabled: Record<string, boolean> = {};
    for (const [id, value] of this.overrides())
      permissionIdToIsEnabled[id] = value;

    this.userService
      .setUserPermissionAllocations(this.userId, permissionIdToIsEnabled)
      .subscribe({
        next: () => {
          this.originalOverrides = new Map(this.overrides());
          this.inheritFromRole.set(this.overrides().size === 0);
          this.saving.set(false);
          this.overridesChanged.emit();
          this.toast.success(
            `Permission overrides saved. This takes effect the next time ${this.userDisplayName} logs in.`,
          );
        },
        error: () => {
          this.saving.set(false);
          this.errorMessage.set(
            'Failed to save permission overrides. Please try again.',
          );
        },
      });
  }

  // ─── "Inherit from role" master switch ─────────────────────────────────────
  onInheritFromRoleToggle(change: MatCheckboxChange): void {
    if (change.checked) {
      this.confirmClearOverrides();
    } else {
      this.inheritFromRole.set(false);
    }
  }

  private confirmClearOverrides(): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        restoreFocus: false,
        data: {
          title: 'Inherit Permissions From Role',
          message:
            `This will remove every direct permission override ${this.userDisplayName} has and fully ` +
            `restore inheritance from their role(s). This cannot be undone automatically — you'd need ` +
            `to re-add each override by hand. Continue?`,
          confirmLabel: 'Remove Overrides',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.clearAllOverrides();
      });
  }

  private clearAllOverrides(): void {
    this.clearing.set(true);
    this.errorMessage.set(null);

    this.userService.setUserPermissionAllocations(this.userId, {}).subscribe({
      next: () => {
        this.originalOverrides = new Map();
        this.overrides.set(new Map());
        this.inheritFromRole.set(true);
        this.clearing.set(false);
        this.overridesChanged.emit();
        this.toast.success(
          `All direct overrides removed. ${this.userDisplayName} now fully inherits role permissions.`,
        );
      },
      error: () => {
        this.clearing.set(false);
        this.errorMessage.set(
          'Failed to remove direct permission overrides. Please try again.',
        );
      },
    });
  }
}
