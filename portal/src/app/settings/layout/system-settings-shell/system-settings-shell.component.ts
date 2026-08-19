import { Component, inject, computed } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { AuthStore } from '../../../auth/store/auth.store';

interface SystemSettingsSection {
  label: string;
  route: string;
  icon: string;
  /** Omit for sections every viewer who reached this shell may see. */
  permissions?: string[];
  /** Hidden unless the user has the SuperAdmin role — General/Security have no permission of their
   *  own (AllowedCorsOriginsController-style backend policies gate them by role, not permission). */
  superAdminOnly?: boolean;
}

// Kept in sync with settings.routes.ts's per-section guards below this shell — Email and the four
// Terminology Codes systems are independently permission-controlled, so a role holding only
// loinc.view (say) must see Terminology Codes here without also seeing Email/General/Security.
const SYSTEM_SETTINGS_SECTIONS: SystemSettingsSection[] = [
  { label: 'Email', route: 'email', icon: 'mail', permissions: ['configuration.view', 'configuration.write'] },
  { label: 'General', route: 'general', icon: 'tune', superAdminOnly: true },
  { label: 'Security', route: 'security', icon: 'security', superAdminOnly: true },
  {
    label: 'Terminology Codes', route: 'terminology', icon: 'biotech',
    permissions: [
      'loinc.view', 'loinc.write', 'snomedct.view', 'snomedct.write', 'rxnorm.view', 'rxnorm.write', 'icd10.view', 'icd10.write',
    ],
  },
];

@Component({
  selector: 'app-system-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './system-settings-shell.component.html',
  styleUrl: './system-settings-shell.component.scss',
})
export class SystemSettingsShellComponent {
  private readonly store = inject(AuthStore);

  // Same visibility rule as settings-shell.component.ts's own tab list — kept in sync deliberately
  // so a section only appears here if the user could also reach it via this shell's own route guard.
  readonly sections = computed<SystemSettingsSection[]>(() =>
    SYSTEM_SETTINGS_SECTIONS.filter(section => {
      if (section.superAdminOnly) return this.store.hasRole('SuperAdmin');
      if (!section.permissions?.length) return true;
      if (this.store.isAdmin()) return true;
      return section.permissions.some(p => this.store.hasPermission(p));
    })
  );
}
