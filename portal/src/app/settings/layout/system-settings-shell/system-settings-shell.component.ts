import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Route component for Settings → System Settings.
 *
 * It used to render a tab strip (Email / General / Security / SSO Configurations / Terminology Codes) with
 * per-tab permission filtering. Email, Security and SSO Configurations are now launcher rows on General —
 * opened as full dialogs via SettingsPageDialogService, each keeping the exact permission its tab had — and
 * Terminology Codes is feature-flagged off (data/terminology-feature.config.ts). That leaves General as the
 * only section, so the strip would render a single tab pointing at the page already on screen.
 *
 * The shell is kept rather than collapsed away because the route still needs a component to host the parent
 * permission gate and the child routes (which remain, so deep links like /settings/system-settings/email
 * keep resolving). It simply renders its child directly now.
 *
 * To restore tabs: put the nav back in the template alongside a section list, and swap the '' child's plain
 * `redirectTo: 'general'` in settings.routes.ts back to a settingsLandingGuard over the real sections.
 */
@Component({
  selector: 'app-system-settings-shell',
  standalone: true,
  imports: [RouterOutlet],
  templateUrl: './system-settings-shell.component.html',
  styleUrl: './system-settings-shell.component.scss',
})
export class SystemSettingsShellComponent {}
