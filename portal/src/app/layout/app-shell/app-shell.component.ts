import { Component, signal, inject, OnInit, HostListener } from '@angular/core';
import { RouterOutlet, RouterLink, Router, NavigationEnd } from '@angular/router';
import { filter } from 'rxjs/operators';
import { SidebarComponent } from '../../dashboard/layout/sidebar/sidebar.component';
import { UserMenuComponent } from '../../user/components/user-menu/user-menu.component';
import { AppFooterComponent } from '../app-footer/app-footer.component';
import { UnsavedChangesRegistryService } from '../../core/services/unsaved-changes-registry.service';
import { DialogOutletComponent } from '../../core/components/dialog-outlet/dialog-outlet.component';
import { AppInitService } from '../../onboarding/services/app-init.service';
import { AuthService } from '../../auth/services/auth.service';

const PAGE_TITLES: Record<string, string> = {
  '/dashboard':                    'Dashboard',
  '/workflow-builder':             'Workflow Builder',
  '/user-management':              'User Management',
  '/user-management/tenants':      'Tenant',
  '/user-management/roles':        'Role',
  '/profile':                      'My Profile',
  '/account-settings':             'Account Settings',
  '/security':                     'Security',
  '/preferences':                  'Preferences',
  '/help':                         'Help & Support',
  '/execution-history':            'Execution History',
  '/logs':                         'Execution Logs',
  '/schedules':                    'Schedules',
  '/reports':                      'Reports',
  '/config':                       'Configuration',
  '/settings':                     'Settings',
  '/settings/branding':            'Branding',
};

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, SidebarComponent, UserMenuComponent, AppFooterComponent, DialogOutletComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
})
export class AppShellComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);
  private readonly appInit = inject(AppInitService);
  private readonly auth = inject(AuthService);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly pageTitle        = signal('Dashboard');

  // Server-enforced license gate's UI mirror (see AppInitService.refreshLicenseGate / Program.cs's
  // license-gate middleware) — every non-exempt API action already 403s while this is false; this just
  // makes the reason visible instead of leaving the admin to guess from a wall of failed requests.
  protected readonly licenseActive = this.appInit.licenseActive;
  protected readonly licenseState  = this.appInit.licenseState;

  // Suppresses the working-area overlay on the License Settings page itself — otherwise an inactive
  // license would make the one screen that lets an admin fix it unreachable too.
  protected readonly onLicenseSettingsPage = signal(false);

  protected isAdminUser(): boolean {
    return this.auth.isAdmin();
  }

  // Tab close, refresh, or browser Back/Forward moving past the SPA's own history entirely — the
  // in-app CanDeactivate guard (unsaved-changes.guard.ts) can't intercept any of those. Browsers
  // ignore any custom message here and show their own generic prompt; that's a platform constraint,
  // not something we can override.
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.unsavedChangesRegistry.hasAnyUnsavedChanges()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  ngOnInit(): void {
    this.updateTitle(this.router.url);
    this.router.events
      .pipe(filter(e => e instanceof NavigationEnd))
      .subscribe((e: NavigationEnd) => this.updateTitle(e.urlAfterRedirects));
  }

  private updateTitle(url: string): void {
    const cleanUrl = url.split('?')[0];
    this.onLicenseSettingsPage.set(cleanUrl.startsWith('/settings/license'));

    if (PAGE_TITLES[cleanUrl]) {
      this.pageTitle.set(PAGE_TITLES[cleanUrl]);
      return;
    }
    const base = '/' + cleanUrl.split('/')[1];
    this.pageTitle.set(PAGE_TITLES[base] ?? 'Segue');
  }

  toggleSidebar(): void {
    this.sidebarCollapsed.update(v => !v);
  }
}
