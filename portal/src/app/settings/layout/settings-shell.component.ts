import { Component, inject, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';
import { IRoleService } from '../../user-management/services/i-role.service';
import { TERMINOLOGY_FEATURE_ENABLED, TERMINOLOGY_PERMISSION_CODES } from '../../data/terminology-feature.config';

interface SettingsTab {
  label: string;
  route: string;
  icon: string;
  /** Omit for tabs every authenticated user with settings access may see. */
  permissions?: string[];
  /** Hidden unless the user is SuperAdmin or holds a role with Full System Access — stricter than
   *  `permissions`, which a regular Admin also satisfies via isAdmin(). Takes precedence over
   *  `permissions`. */
  superAdminOnly?: boolean;
}

const SETTINGS_TABS: SettingsTab[] = [
  { label: 'Branding', route: 'branding', icon: 'palette', permissions: ['configuration.write'] },
  // Merged tab covering the former standalone Source Connections / Destination Connections / Mapping
  // Profiles / Transformation Rules tabs — see settings.routes.ts's 'workflow-configurations' route
  // for the sections underneath. Kept in sync with that route's own OR-list (each of the four moved
  // to its own dedicated View permission — this used to only list two of them, which hid this tab
  // from e.g. a Destination-Connections-only role even though the route itself would let them in).
  { label: 'Workflow Configurations', route: 'workflow-configurations', icon: 'account_tree', permissions: ['sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view'] },
  { label: 'EHR Endpoints', route: 'ehr-endpoints', icon: 'hub', permissions: ['ehrendpoints.view'] },
  { label: 'Allowed Origins', route: 'allowed-origins', icon: 'public', superAdminOnly: true },
  // Merged tab covering the former standalone Email Settings / System Security / System Settings /
  // Terminology Codes tabs — see settings.routes.ts's 'system-settings' route for the sections
  // underneath. NOT superAdminOnly: Email and the four Terminology Codes systems are independently
  // permission-controlled and must be reachable without the SuperAdmin role; General/Security are
  // still SuperAdmin-role-only, but that's enforced by their OWN route guards and by
  // system-settings-shell.component.ts's own section filtering, not by hiding this whole tab.
  {
    label: 'System Settings', route: 'system-settings', icon: 'tune',
    // Terminology's codes only count toward this tab's visibility while the feature is enabled —
    // see data/terminology-feature.config.ts; otherwise a terminology-only role would see this tab
    // but find nothing reachable inside it.
    permissions: [
      'configuration.view', 'configuration.write',
      ...(TERMINOLOGY_FEATURE_ENABLED ? TERMINOLOGY_PERMISSION_CODES : []),
    ],
  },
];

@Component({
  selector: 'app-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './settings-shell.component.html',
  styleUrl: './settings-shell.component.scss',
})
export class SettingsShellComponent {
  private readonly store   = inject(AuthStore);
  private readonly roleSvc = inject(IRoleService);

  // RBAC Fix 5: whether the current caller holds Full System Access via any of their own roles —
  // resolved the same way role-dialog.component.ts's callerHasFullAccess / super-admin.guard.ts /
  // settings-landing.guard.ts do: the real per-role IsFullAccess flag from IRoleService.getRoles(),
  // cross-referenced by name against the roles this session's own claims say it holds — never a
  // hardcoded role name for this capability. Starts false (fails closed) until the async check
  // resolves or if it ever errors, exactly like a caller who simply isn't SuperAdmin today; the
  // superAdminOnly tab stays hidden either way, never shown speculatively.
  private readonly callerHasFullAccess = signal(false);

  constructor() {
    // A literal SuperAdmin claim already satisfies the OR below on its own — skip the extra API call
    // entirely for that common case, exactly as super-admin.guard.ts/settings-landing.guard.ts do.
    if (this.store.hasRole('SuperAdmin')) return;

    const heldRoleNames = new Set(this.store.roles().map(r => r.displayName));
    this.roleSvc.getRoles().subscribe({
      next: allRoles => this.callerHasFullAccess.set(allRoles.some(r => heldRoleNames.has(r.name) && r.isFullAccess)),
      error: () => this.callerHasFullAccess.set(false),
    });
  }

  // Same visibility rule as the sidebar (sidebar.component.ts) — kept in sync deliberately so a
  // tab only appears here if the user could also reach it from the sidebar's old direct links.
  readonly tabs = computed<SettingsTab[]>(() =>
    SETTINGS_TABS.filter(tab => {
      if (tab.superAdminOnly) return this.store.hasRole('SuperAdmin') || this.callerHasFullAccess();
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
