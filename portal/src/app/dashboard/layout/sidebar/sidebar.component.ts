import { Component, input, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthStore } from '../../../auth/store/auth.store';

interface NavItem {
  type: 'item';
  icon: string;
  label: string;
  route: string;
  exact?: boolean;
  /** Omit for entries every authenticated user may see; otherwise hidden unless the user has one of these. */
  permissions?: string[];
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
  { type: 'item', icon: '⚡', label: 'Pipeline Builder', route: '/workflow-builder' },
  { type: 'item', icon: '🗂', label: 'Workflows',        route: '/workflows' },
  { type: 'item', icon: '▶',  label: 'Pipelines',        route: '/pipelines' },
  { type: 'item', icon: '📋', label: 'Activity Feed',    route: '/activity' },
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

  // Same visibility rule as permissionGuard: admins always pass; otherwise the item needs at
  // least one of its required permissions (no `permissions` = visible to everyone logged in).
  readonly navEntries = computed<NavEntry[]>(() =>
    NAV_ENTRIES.filter(entry => {
      if (this.isSection(entry)) return true;
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
