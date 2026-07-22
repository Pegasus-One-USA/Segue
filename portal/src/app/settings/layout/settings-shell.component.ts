import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../../auth/store/auth.store';

interface SettingsTab {
  label: string;
  route: string;
  /** Omit for tabs every authenticated user with settings access may see. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — stricter than `permissions`, which a
   *  regular Admin also satisfies via isAdmin(). Takes precedence over `permissions`. */
  superAdminOnly?: boolean;
}

const SETTINGS_TABS: SettingsTab[] = [
  { label: 'Branding', route: 'branding', permissions: ['configuration.write'] },
  { label: 'EHR Endpoints', route: 'ehr-endpoints', permissions: ['configuration.write'] },
  { label: 'Source Connections', route: 'source-connections', permissions: ['sourceconnections.view'] },
  { label: 'Destination Connections', route: 'destination-connections', permissions: ['configuration.write'] },
  { label: 'Allowed Origins', route: 'allowed-origins', superAdminOnly: true },
  { label: 'System Security', route: 'system-security', superAdminOnly: true },
];

@Component({
  selector: 'app-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './settings-shell.component.html',
  styleUrl: './settings-shell.component.scss',
})
export class SettingsShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as the sidebar (sidebar.component.ts) — kept in sync deliberately so a
  // tab only appears here if the user could also reach it from the sidebar's old direct links.
  readonly tabs = computed<SettingsTab[]>(() =>
    SETTINGS_TABS.filter(tab => {
      if (tab.superAdminOnly) return this.store.hasRole('SuperAdmin');
      if (!tab.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return tab.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
