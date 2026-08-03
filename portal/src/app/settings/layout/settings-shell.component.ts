import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../auth/store/auth.store';

interface SettingsTab {
  label: string;
  route: string;
  icon: string;
  /** Omit for tabs every authenticated user with settings access may see. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — stricter than `permissions`, which a
   *  regular Admin also satisfies via isAdmin(). Takes precedence over `permissions`. */
  superAdminOnly?: boolean;
}

const SETTINGS_TABS: SettingsTab[] = [
  { label: 'Branding', route: 'branding', icon: 'palette', permissions: ['configuration.write'] },
  // Merged tab covering the former standalone Source Connections / Destination Connections / Mapping
  // Profiles tabs — see settings.routes.ts's 'workflow-configurations' route for the sections underneath.
  { label: 'Workflow Configurations', route: 'workflow-configurations', icon: 'account_tree', permissions: ['sourceconnections.view', 'configuration.write'] },
  { label: 'EHR Endpoints', route: 'ehr-endpoints', icon: 'hub', permissions: ['configuration.write'] },
  { label: 'Allowed Origins', route: 'allowed-origins', icon: 'public', superAdminOnly: true },
  // Merged tab covering the former standalone Email Settings / System Security / System Settings tabs —
  // see settings.routes.ts's 'system-settings' route for the Email/General/Security sections underneath.
  { label: 'System Settings', route: 'system-settings', icon: 'tune', superAdminOnly: true },
];

@Component({
  selector: 'app-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
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
