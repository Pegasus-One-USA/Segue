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
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import {
  TriStateToggleComponent,
  TriState,
} from './tri-state-toggle.component';
import { ToastService } from '../../../services/toast.service';
import { AuthService } from '../../../auth/services/auth.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PermissionGroup, PermissionAction as PermCode, permissionCode } from '../../../auth/models/permission.constants';
import { PhaseConfigService } from '../../../services/phase-config.service';
import { isNodePermissionPrefixVisible } from '../../../data/node-permission-visibility.util';

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
    HideWithoutPermissionDirective,
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
  private readonly customDialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly authService = inject(AuthService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly phaseCfg = inject(PhaseConfigService);

  // ─── Permission gating — direct overrides are a form of editing the user, so they're gated on
  // the same user.edit code as the rest of this user's mutating actions (Edit/Reset Password/2FA
  // toggle in the parent user-detail component). ────────────────────────────────────────────────
  protected readonly PermissionGroup = PermissionGroup;
  protected readonly PermissionAction = PermCode;
  protected readonly permissionCode = permissionCode;

  canEdit(): boolean {
    return this.authService.isAdmin() || this.authService.hasPermission(permissionCode(PermissionGroup.User, PermCode.Edit));
  }

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

  // Drops any group for a source/destination node the Workflow Builder itself doesn't let anyone
  // add yet (phase-gated — see PhaseConfigService/node-permission-visibility.util.ts), the same
  // filter role-permissions.component.ts's own Workflow Nodes table and Effective Permissions
  // already apply — without it, this screen let an admin set direct overrides on a vendor no one
  // can actually reach from a workflow (e.g. Cerner, Sftp). A group with no permissions has no
  // resource to check and is dropped too.
  private readonly gridCategories = computed<GridCategory[]>(() => {
    return this.catalog().map((cat) => {
      const visibleGroups = cat.groups.filter(
        (g) => g.permissions.length > 0 && isNodePermissionPrefixVisible(g.permissions[0].resource, this.phaseCfg),
      );
      const rows: GridRow[] = visibleGroups.map((g) => {
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
    if (!this.canEdit()) return;
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
    if (!this.canEdit()) return;
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
    if (!this.canEdit()) return;
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
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermCode.Edit), 'You do not have permission to edit users.')) return;
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
    if (!this.canEdit()) return;
    if (change.checked) {
      this.confirmClearOverrides();
    } else {
      this.inheritFromRole.set(false);
    }
  }

  private confirmClearOverrides(): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '440px',
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
