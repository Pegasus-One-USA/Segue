import { Component, inject, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';
import { IRoleService } from '../../../user-management/services/i-role.service';
import { TERMINOLOGY_FEATURE_ENABLED } from '../../../data/terminology-feature.config';

interface SystemSettingsSection {
  label: string;
  route: string;
  icon: string;
  /** Omit for sections every viewer who reached this shell may see. */
  permissions?: string[];
  /** Hidden unless the user is SuperAdmin or holds a role with Full System Access — General/Security
   *  have no permission of their own (AllowedCorsOriginsController-style backend policies gate them by
   *  role/IsFullAccess, not permission). */
  superAdminOnly?: boolean;
}

// Kept in sync with settings.routes.ts's per-section guards below this shell — Email is
// independently permission-controlled, so a role holding only configuration.view must see it here
// without also seeing General/Security.
const SYSTEM_SETTINGS_SECTIONS: SystemSettingsSection[] = [
  { label: 'Email', route: 'email', icon: 'mail', permissions: ['configuration.view', 'configuration.write'] },
  { label: 'General', route: 'general', icon: 'tune', superAdminOnly: true },
  { label: 'Security', route: 'security', icon: 'security', superAdminOnly: true },
  // Restored — was previously hidden from navigation while still fully reachable via its route guard
  // (settings.routes.ts), leaving a role that holds only these terminology permissions with no visible
  // way to reach or leave Terminology at all once Email/General/Security/SSO were also hidden for it.
  // Same permission list the route guard already checks — a role needs at least one to see the tab.
  {
    label: 'Terminology Codes', route: 'terminology', icon: 'biotech',
    permissions: [
      'loinc.view', 'loinc.write', 'snomedct.view', 'snomedct.write', 'rxnorm.view', 'rxnorm.write', 'icd10.view', 'icd10.write',
    ],
  },
  // SuperAdmin-role-only, matching settings.routes.ts's own sso-configurations child guard and the
  // backend's SsoConfigurationsController policy.
  { label: 'SSO Configurations', route: 'sso-configurations', icon: 'admin_panel_settings', superAdminOnly: true },
];

@Component({
  selector: 'app-system-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './system-settings-shell.component.html',
  styleUrl: './system-settings-shell.component.scss',
})
export class SystemSettingsShellComponent {
  private readonly store   = inject(AuthStore);
  private readonly roleSvc = inject(IRoleService);

  // RBAC Fix 6: whether the current caller holds Full System Access via any of their own roles —
  // resolved the same way settings-shell.component.ts's callerHasFullAccess (Fix 5) / role-dialog's /
  // both guards' do: the real per-role IsFullAccess flag from IRoleService.getRoles(), cross-referenced
  // by name against the roles this session's own claims say it holds — never a hardcoded role name for
  // this capability. Starts false (fails closed) until the async check resolves or if it ever errors,
  // exactly like a caller who simply isn't SuperAdmin today; General/Security/SSO Configurations stay
  // hidden either way, never shown speculatively.
  private readonly callerHasFullAccess = signal(false);

  constructor() {
    // A literal SuperAdmin claim already satisfies the OR below on its own — skip the extra API call
    // entirely for that common case, exactly as the other Full-Access sites do.
    if (this.store.hasRole('SuperAdmin')) return;

    const heldRoleNames = new Set(this.store.roles().map(r => r.displayName));
    this.roleSvc.getRoles().subscribe({
      next: allRoles => this.callerHasFullAccess.set(allRoles.some(r => heldRoleNames.has(r.name) && r.isFullAccess)),
      error: () => this.callerHasFullAccess.set(false),
    });
  }

  // Same visibility rule as settings-shell.component.ts's own tab list — kept in sync deliberately
  // so a section only appears here if the user could also reach it via this shell's own route guard.
  // Terminology Codes additionally requires the feature flag — see data/terminology-feature.config.ts
  // — since its route is unreachable (featureFlagGuard in settings.routes.ts) while disabled, and this
  // tab would otherwise still show for a role holding terminology permissions.
  readonly sections = computed<SystemSettingsSection[]>(() =>
    SYSTEM_SETTINGS_SECTIONS.filter(section => {
      if (section.route === 'terminology' && !TERMINOLOGY_FEATURE_ENABLED) return false;
      if (section.superAdminOnly) return this.store.hasRole('SuperAdmin') || this.callerHasFullAccess();
      if (!section.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return section.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
