import { Component, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

interface NavItem {
  icon:  string;
  label: string;
  route: string;
}

const NAV_ITEMS: NavItem[] = [
  { icon: '⊞',  label: 'Dashboard',        route: '/dashboard' },
  { icon: '⚡', label: 'Pipeline Builder', route: '/workflow-builder' },
  { icon: '≡',  label: 'Execution Logs',   route: '/logs' },
  { icon: '◷',  label: 'Schedules',        route: '/schedules' },
  { icon: '▤',  label: 'Reports',          route: '/reports' },
  { icon: '⚙',  label: 'Configuration',   route: '/config' },
  { icon: '👤', label: 'User Management',  route: '/user-management' },
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
  readonly navItems  = NAV_ITEMS;
}
