import { Component, input, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthStore } from '../../../auth/store/auth.store';
import { BrandingService } from '../../../services/branding.service';

interface NavItem {
  type: 'item';
  icon: string;
  label: string;
  route: string;
  exact?: boolean;
  /** Omit for entries every authenticated user may see; otherwise hidden unless the user has one of these. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — stricter than `permissions`, which a regular Admin
   *  also satisfies via isAdmin(). Takes precedence over `permissions` when both are set. */
  superAdminOnly?: boolean;
}

interface NavSection {
  type: 'section';
  label: string;
}

type NavEntry = NavItem | NavSection;

const NAV_ENTRIES: NavEntry[] = [
  { type: 'item', icon: '⊞',  label: 'Dashboard',        route: '/dashboard' },
  { type: 'item', icon: '🔐', label: 'Role',             route: '/user-management/roles', permissions: ['role.view'] },
  { type: 'item', icon: '👥', label: 'User Management',  route: '/user-management',          exact: true, permissions: ['user.view'] },
  { type: 'item', icon: '🗂', label: 'Workflows',        route: '/workflows' },
  { type: 'item', icon: '▶',  label: 'Execution History', route: '/execution-history' },
  // Settings hub — Branding/EHR Endpoints/Source & Destination Connections/Allowed Origins/System
  // Security now live as tabs under here (settings-shell.component.ts). Visible to anyone who
  // could reach at least one of those tabs before consolidation — SuperAdmin-only tabs are gated
  // again inside the shell itself, so this entry doesn't need `superAdminOnly` of its own.
  { type: 'item', icon: '⚙',  label: 'Settings',         route: '/settings', permissions: ['configuration.write', 'sourceconnections.view'] },

  // Operations hub — System Health, Pipeline Executions, Queue Monitor, etc. now live as tabs
  // under here (operations-shell.component.ts), replacing the former 12-item sidebar section.
  { type: 'item', icon: '🧭', label: 'Operations',        route: '/operations', permissions: ['governance.read'] },

  // Governance hub — Audit Logs, Correlation Search, Security Events, etc. now live as tabs under
  // here (governance-shell.component.ts), replacing the former 14-item sidebar section.
  { type: 'item', icon: '🛡',  label: 'Governance',        route: '/governance', permissions: ['governance.read'] },
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
  protected readonly branding = inject(BrandingService);

  // Same visibility rule as permissionGuard: admins always pass; otherwise the item needs at
  // least one of its required permissions (no `permissions` = visible to everyone logged in).
  readonly navEntries = computed<NavEntry[]>(() =>
    NAV_ENTRIES.filter(entry => {
      if (this.isSection(entry)) return true;
      if (entry.superAdminOnly) return this.store.hasRole('SuperAdmin');
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
