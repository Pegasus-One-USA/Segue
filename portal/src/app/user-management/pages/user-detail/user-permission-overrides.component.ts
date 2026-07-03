// user-management/pages/user-detail/user-permission-overrides.component.ts
import { Component, OnChanges, SimpleChanges, Input, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { forkJoin } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonToggleModule, MatButtonToggleChange } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IUserService } from '../../../auth/services/i-user.service';
import { IRoleService } from '../../services/i-role.service';
import { Permission, PermissionAllocationDto } from '../../../auth/models/user.model';

/** null = no override (inherits role grants); true = explicit grant; false = explicit deny. */
type OverrideState = boolean | null;

interface PermissionGroup {
  resource: string;
  permissions: Permission[];
}

@Component({
  selector: 'app-user-permission-overrides',
  standalone: true,
  imports: [
    CommonModule,
    MatIconModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './user-permission-overrides.component.html',
  styleUrls: ['./user-permission-overrides.component.scss'],
})
export class UserPermissionOverridesComponent implements OnChanges {
  // Bound from the parent user-detail component.
  @Input() userId!: string;
  @Input() userDisplayName = 'this user';

  private readonly userService = inject(IUserService);
  private readonly roleService = inject(IRoleService);
  private readonly snack       = inject(MatSnackBar);

  readonly loading = signal(true);
  readonly savingId = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  readonly allPermissions = signal<Permission[]>([]);
  // permissionId -> override state.
  readonly overrides = signal<Map<string, OverrideState>>(new Map());

  readonly permissionGroups = computed<PermissionGroup[]>(() => {
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

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['userId'] && this.userId) {
      this.loadData();
    }
  }

  private loadData(): void {
    this.loading.set(true);
    this.errorMessage.set(null);

    forkJoin({
      permissions: this.roleService.getPermissions(),
      allocations: this.userService.getUserPermissionAllocations(this.userId),
    }).subscribe({
      next: ({ permissions, allocations }) => {
        this.allPermissions.set(permissions);
        this.overrides.set(this.toOverrideMap(allocations));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set('Failed to load direct permission overrides.');
      },
    });
  }

  private toOverrideMap(allocations: PermissionAllocationDto[]): Map<string, OverrideState> {
    const map = new Map<string, OverrideState>();
    for (const a of allocations) {
      map.set(a.permissionId, a.isEnabled);
    }
    return map;
  }

  stateFor(permissionId: string): OverrideState {
    return this.overrides().get(permissionId) ?? null;
  }

  isSaving(permissionId: string): boolean {
    return this.savingId() === permissionId;
  }

  onToggle(permissionId: string, change: MatButtonToggleChange): void {
    const value = change.value as 'inherit' | 'grant' | 'deny';
    const nextState: OverrideState = value === 'inherit' ? null : value === 'grant';

    this.savingId.set(permissionId);

    const done = (map: Map<string, OverrideState>) => {
      this.overrides.set(map);
      this.savingId.set(null);
      this.snack.open(
        `This change will take effect the next time ${this.userDisplayName} logs in.`,
        'Dismiss',
        { duration: 4000 },
      );
    };

    const fail = (message: string) => {
      this.savingId.set(null);
      this.snack.open(message, 'Dismiss', { duration: 4000 });
    };

    if (nextState === null) {
      this.userService.removeUserPermissionAllocation(this.userId, permissionId).subscribe({
        next: () => {
          const next = new Map(this.overrides());
          next.delete(permissionId);
          done(next);
        },
        error: () => fail('Failed to reset this permission to inherit from role.'),
      });
    } else {
      this.userService.setUserPermissionAllocation(this.userId, permissionId, nextState).subscribe({
        next: () => {
          const next = new Map(this.overrides());
          next.set(permissionId, nextState);
          done(next);
        },
        error: () => fail(nextState ? 'Failed to grant this permission.' : 'Failed to deny this permission.'),
      });
    }
  }
}
