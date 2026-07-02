import { Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

interface NavItem {
  type: 'item';
  icon: string;
  label: string;
  route: string;
  exact?: boolean;
}

interface NavSection {
  type: 'section';
  label: string;
}

type NavEntry = NavItem | NavSection;

const NAV_ENTRIES: NavEntry[] = [
  { type: 'item', icon: '⊞',  label: 'Dashboard',        route: '/dashboard' },
  { type: 'item', icon: '🔐', label: 'Role',             route: '/user-management/roles' },
  { type: 'item', icon: '👥', label: 'Users',            route: '/user-management',          exact: true },
  { type: 'item', icon: '⚡', label: 'Pipeline Builder', route: '/workflow-builder' },
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
  readonly collapsed  = input(false);
  readonly navEntries = NAV_ENTRIES;

  isItem(entry: NavEntry): entry is NavItem {
    return entry.type === 'item';
  }

  isSection(entry: NavEntry): entry is NavSection {
    return entry.type === 'section';
  }
}
