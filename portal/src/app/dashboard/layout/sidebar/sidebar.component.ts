import { Component, input, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthStore } from '../../../auth/store/auth.store';
import { BrandingService } from '../../../services/branding.service';
import { PermissionService } from '../../../auth/services/permission.service';

interface NavItem {
  type: 'item';
  label: string;
  route: string;
  exact?: boolean;
  /** Omit for entries every authenticated user may see; otherwise hidden unless the user has one of these. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — stricter than `permissions`, which a regular Admin
   *  also satisfies via isAdmin(). Takes precedence over `permissions` when both are set. */
  superAdminOnly?: boolean;
  /** Overrides `permissions` entirely when present — for an entry whose visibility isn't a plain "holds
   *  any of these exact codes" OR-list (e.g. Workflows, gated on PermissionService.hasWorkflowModuleAccess()
   *  — workflow.view OR any workflow-node permission). Receives PermissionService because NAV_ENTRIES is a
   *  module-level const evaluated before any injector exists — the component's navEntries computed calls
   *  this with its own injected instance. */
  customCheck?: (permissions: PermissionService) => boolean;
}

interface NavSection {
  type: 'section';
  label: string;
}

type NavEntry = NavItem | NavSection;

const NAV_ENTRIES: NavEntry[] = [
  // Dashboard is a mandatory platform entry point, not an RBAC-controlled permission — every
  // authenticated role can see it regardless of workflow.view (no `permissions` = visible to
  // everyone logged in, per navEntries' filter below). Its stat tiles/"Recent Workflows" widget
  // still call the same /workflow-runs[/stats] endpoints Workflows itself uses (see
  // WorkflowEndpoints.cs), which stay workflow.view-gated — a role without it still reaches this
  // page, just sees those specific widgets come back empty rather than erroring (see
  // dashboard.component.ts/pipeline-run.service.ts's permission-aware error handling).
  { type: 'item', label: 'Dashboard',        route: '/dashboard' },
  // Role management is independent of User Management: /user-management/roles is reachable with
  // role.view alone (the parent /user-management route admits user.view OR role.view, and each
  // user-DATA child re-guards user.view on its own — see app.routes.ts / user-management.routes.ts).
  // So the link matches route reachability with a plain role.view OR-list.
  { type: 'item', label: 'Role',             route: '/user-management/roles', permissions: ['role.view'] },
  { type: 'item', label: 'User Management',  route: '/user-management',          exact: true, permissions: ['user.view'] },
  // Module access: workflow.view OR any workflow-node permission (epic.*, sqlserver.*, ...) — a role
  // holding only e.g. epic.view must still see this entry, since node access implies module access
  // (see PermissionService.hasWorkflowModuleAccess, the single shared source of truth also used by the
  // /workflows and /workflow-builder route guards and the Node Library's open-gate).
  { type: 'item', label: 'Workflows',        route: '/workflows', customCheck: p => p.hasWorkflowModuleAccess() },
  { type: 'item', label: 'Execution History', route: '/execution-history', permissions: ['workflow.view'] },
  // Settings hub — Branding/EHR Endpoints/Source & Destination Connections/Mapping Profiles/
  // Transformation Rules/Allowed Origins/System Settings (Email + Terminology Codes' four
  // independent systems) now live as tabs under here (settings-shell.component.ts). Visible to
  // anyone who could reach at least one of those tabs — every OR-list here must stay in sync with
  // settings.routes.ts's own per-tab permissions, or a tab someone can actually open becomes
  // unreachable because the sidebar link itself never appears (see settings-shell.component.ts's
  // SETTINGS_TABS/system-settings-shell.component.ts's SYSTEM_SETTINGS_SECTIONS for the same list,
  // duplicated at each layer for the same reason the sidebar and route guards are always kept in
  // sync elsewhere). SuperAdmin-only tabs (Allowed Origins, System Settings -> General/Security) are
  // gated again inside the shells themselves, so this entry doesn't need `superAdminOnly` of its own.
  {
    type: 'item', label: 'Settings', route: '/settings',
    permissions: [
      'configuration.view', 'configuration.write',
      'sourceconnections.view', 'destinationconnections.view', 'mappingprofiles.view', 'transformationrules.view',
      'ehrendpoints.view',
      'loinc.view', 'loinc.write', 'snomedct.view', 'snomedct.write', 'rxnorm.view', 'rxnorm.write', 'icd10.view', 'icd10.write',
    ],
  },

  // Logs & Compliance hub — merges the former separate Operations and Governance sidebar entries
  // into one, per user direction, since only a handful of tabs remain visible across both shells
  // (Audit Logs/Correlation Search/Compliance Reports live under governance-shell.component.ts;
  // Errors lives under operations-shell.component.ts and is cross-linked from there via an absolute
  // route). Lands on /governance, which now surfaces Errors as one of its tabs.
  { type: 'item', label: 'Logs & Compliance', route: '/governance', permissions: ['governance.read'] },
];

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
  readonly collapsed = input(false);

  private readonly store = inject(AuthStore);
  private readonly permissions = inject(PermissionService);
  protected readonly branding = inject(BrandingService);

  // Same visibility rule as permissionGuard: admins always pass; otherwise the item needs at
  // least one of its required permissions (no `permissions` = visible to everyone logged in).
  readonly navEntries = computed<NavEntry[]>(() =>
    NAV_ENTRIES.filter(entry => {
      if (this.isSection(entry)) return true;
      if (entry.superAdminOnly) return this.store.hasRole('SuperAdmin');
      if (entry.customCheck) return entry.customCheck(this.permissions);
      if (!entry.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return entry.permissions.some(p => this.store.hasPermission(p));
    })
  );

  isItem(entry: NavEntry): entry is NavItem {
    return entry.type === 'item';
  }

  isSection(entry: NavEntry): entry is NavSection {
    return entry.type === 'section';
  }
}
