import { Component, signal, inject, OnInit } from '@angular/core';
import { RouterOutlet, Router, NavigationEnd } from '@angular/router';
import { filter } from 'rxjs/operators';
import { SidebarComponent } from '../../dashboard/layout/sidebar/sidebar.component';
import { UserMenuComponent } from '../../user/components/user-menu/user-menu.component';

const PAGE_TITLES: Record<string, string> = {
  '/dashboard':                    'Dashboard',
  '/workflow-builder':             'Pipeline Builder',
  '/user-management':              'User Management',
  '/user-management/tenants':      'Tenant',
  '/user-management/roles':        'Role',
  '/profile':                      'My Profile',
  '/account-settings':             'Account Settings',
  '/security':                     'Security',
  '/preferences':                  'Preferences',
  '/help':                         'Help & Support',
  '/execution-history':            'Execution History',
  '/activity':                     'Activity Feed',
  '/logs':                         'Execution Logs',
  '/schedules':                    'Schedules',
  '/reports':                      'Reports',
  '/config':                       'Configuration',
};

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent, UserMenuComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
})
export class AppShellComponent implements OnInit {
  private readonly router = inject(Router);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly pageTitle        = signal('Dashboard');

  ngOnInit(): void {
    this.updateTitle(this.router.url);
    this.router.events
      .pipe(filter(e => e instanceof NavigationEnd))
      .subscribe((e: NavigationEnd) => this.updateTitle(e.urlAfterRedirects));
  }

  private updateTitle(url: string): void {
    const cleanUrl = url.split('?')[0];
    if (PAGE_TITLES[cleanUrl]) {
      this.pageTitle.set(PAGE_TITLES[cleanUrl]);
      return;
    }
    const base = '/' + cleanUrl.split('/')[1];
    this.pageTitle.set(PAGE_TITLES[base] ?? 'FHIRBridge');
  }

  toggleSidebar(): void {
    this.sidebarCollapsed.update(v => !v);
  }
}
