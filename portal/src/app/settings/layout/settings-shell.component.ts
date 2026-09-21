import { Component, inject, computed, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';
import { FullAccessResolverService } from '../../auth/services/full-access-resolver.service';
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
  /** Hidden unless the user has SuperAdmin or Admin (AuthStore.isAdmin()) — matches the backend's
   *  AuthorizationPolicies.UnifiedAdmin policy (LicenseController). Unlike `superAdminOnly`, this also
   *  admits a plain Admin. Takes precedence over `permissions`, same as `superAdminOnly`. */
  /** Currently unused — License was this flag's only consumer before it moved to a launcher row on
   *  System Settings > General. Kept because it is generic tab-visibility infrastructure, and the
   *  filter below still honours it for any future UnifiedAdmin-gated tab. */
  unifiedAdminOnly?: boolean;
}

const SETTINGS_TABS: SettingsTab[] = [
  { label: 'Branding', route: 'branding', icon: 'palette', permissions: ['configuration.write'] },
  // Merged tab covering the former standalone Source Connections / Destination Connections / Mapping
  // Profiles / Transformation Rules tabs — see settings.routes.ts's 'workflow-configurations' route
  // for the sections underneath. Kept in sync with that route's own OR-list (each of the four moved
  // to its own dedicated View permission — this used to only list two of them, which hid this tab
  // from e.g. a Destination-Connections-only role even though the route itself would let them in).
  { label: 'Workflow Configurations', route: 'workflow-configurations', icon: 'account_tree', permissions: ['sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view'] },
  // EHR Endpoints and Allowed Origins are no longer tabs here — both are launcher rows on
  // System Settings > General, opened as full dialogs (SettingsPageDialogService). Their routes were
  // removed, so leaving the tabs would point at paths that no longer resolve.
  // License is no longer a tab here either — it is a launcher row on System Settings > General, gated
  // superAdminOnly, and its route was removed.
  //
  // System Settings now lands directly on its General row list (its sub-tab strip is gone: Email,
  // Security and SSO Configurations became launcher rows on General, and Terminology Codes is
  // feature-flagged off). NOT superAdminOnly: Email and the Terminology Codes systems are independently
  // permission-controlled and must be reachable without the SuperAdmin role, as is the EHR Endpoints
  // row; the restricted rows are gated individually inside the page (SystemSettingListComponent),
  // never by hiding this whole tab.
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
  private readonly store         = inject(AuthStore);
  private readonly fullAccessSvc = inject(FullAccessResolverService);

  // RBAC Fix 5: whether the current caller holds Full System Access via any of their own roles —
  // resolved via the shared FullAccessResolverService (see that file for why it matches by role
  // NAME, never displayName or id), cross-referenced against the roles this session's own claims say
  // it holds. Starts false (fails closed) until the async check resolves or if it ever errors,
  // exactly like a caller who simply isn't SuperAdmin today; the superAdminOnly tab stays hidden
  // either way, never shown speculatively.
  private readonly callerHasFullAccess = signal(false);

  constructor() {
    // A literal SuperAdmin claim already satisfies the OR below on its own — skip the extra API call
    // entirely for that common case, exactly as super-admin.guard.ts/settings-landing.guard.ts do.
    if (this.store.hasRole('SuperAdmin')) return;

    const heldRoleNames = new Set(this.store.roles().map(r => r.name));
    this.fullAccessSvc.resolve(heldRoleNames).subscribe(hasFullAccess => this.callerHasFullAccess.set(hasFullAccess));
  }

  // Same visibility rule as the sidebar (sidebar.component.ts) — kept in sync deliberately so a
  // tab only appears here if the user could also reach it from the sidebar's old direct links.
  readonly tabs = computed<SettingsTab[]>(() =>
    SETTINGS_TABS.filter(tab => {
      if (tab.unifiedAdminOnly) return this.store.isAdmin();
      if (tab.superAdminOnly) return this.store.hasRole('SuperAdmin') || this.callerHasFullAccess();
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
