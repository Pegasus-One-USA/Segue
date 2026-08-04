import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

interface SystemSettingsSection {
  label: string;
  route: string;
  icon: string;
}

// Every SuperAdmin who can reach this shell (see settings.routes.ts's superAdminGuard) can see all
// three sections — unlike the outer SETTINGS_TABS, nothing here needs a further permission split.
const SYSTEM_SETTINGS_SECTIONS: SystemSettingsSection[] = [
  { label: 'Email', route: 'email', icon: 'mail' },
  { label: 'General', route: 'general', icon: 'tune' },
  { label: 'Security', route: 'security', icon: 'security' },
  { label: 'LOINC', route: 'loinc', icon: 'biotech' },
];

@Component({
  selector: 'app-system-settings-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './system-settings-shell.component.html',
  styleUrl: './system-settings-shell.component.scss',
})
export class SystemSettingsShellComponent {
  readonly sections = SYSTEM_SETTINGS_SECTIONS;
}
