import { Component, signal, computed, inject, OnInit, HostListener } from '@angular/core';
import { RouterOutlet, Router, NavigationEnd } from '@angular/router';
import { filter } from 'rxjs/operators';
import { SidebarComponent } from '../../dashboard/layout/sidebar/sidebar.component';
import { UserMenuComponent } from '../../user/components/user-menu/user-menu.component';
import { AppFooterComponent } from '../app-footer/app-footer.component';
import { UnsavedChangesRegistryService } from '../../core/services/unsaved-changes-registry.service';
import { DialogOutletComponent } from '../../core/components/dialog-outlet/dialog-outlet.component';
import { AppInitService } from '../../onboarding/services/app-init.service';
import { AuthService } from '../../auth/services/auth.service';
import { SettingsPageDialogService } from '../../settings/services/settings-page-dialog.service';
import { LayoutService } from '../../services/layout.service';

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
  // RouterLink dropped: the only links here were the two license-gate deep links, now dialog-opening buttons.
  imports: [RouterOutlet, SidebarComponent, UserMenuComponent, AppFooterComponent, DialogOutletComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
})
export class AppShellComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly settingsPageDialog = inject(SettingsPageDialogService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);
  private readonly appInit = inject(AppInitService);
  private readonly auth = inject(AuthService);
  private readonly layoutSvc = inject(LayoutService);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly pageTitle        = signal('Dashboard');
  // Focused (Medplum-inspired): no topbar, no footer, account menu moves to the foot of the sidebar
  // instead — see LayoutService, SidebarComponent and UserMenuComponent's anchor='sidebar' mode.
  protected readonly layoutFocused     = computed(() => this.layoutSvc.mode() === 'focused');

  // Server-enforced license gate's UI mirror (see AppInitService.refreshLicenseGate / Program.cs's
  // license-gate middleware) — every non-exempt API action already 403s while this is false; this just
  // makes the reason visible instead of leaving the admin to guess from a wall of failed requests.
  protected readonly licenseActive = this.appInit.licenseActive;
  protected readonly licenseState  = this.appInit.licenseState;

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

    if (PAGE_TITLES[cleanUrl]) {
      this.pageTitle.set(PAGE_TITLES[cleanUrl]);
      return;
    }
    const base = '/' + cleanUrl.split('/')[1];
    this.pageTitle.set(PAGE_TITLES[base] ?? 'Segue');
  }

  /** License moved off its own route onto a dialog (Settings > System Settings > General). Both license-gate
   *  banners open it here rather than deep-linking to a route that no longer exists. */
  protected openLicenseDialog(): void {
    this.settingsPageDialog.open('license');
  }

  toggleSidebar(): void {
    this.sidebarCollapsed.update(v => !v);
  }
}
