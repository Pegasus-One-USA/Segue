import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

export type SettingsPage =
  | 'profile' | 'account-settings' | 'security'
  | 'preferences' | 'help';

interface NavItem {
  id: SettingsPage;
  label: string;
  sub: string;
  path: string;
}

@Component({
  selector:    'app-user-settings-nav',
  standalone:  true,
  imports:     [RouterLink],
  templateUrl: './user-settings-nav.component.html',
  styleUrl:    './user-settings-nav.component.scss',
})
export class UserSettingsNavComponent {
  readonly activePage = input<SettingsPage>('profile');

  protected readonly navItems: NavItem[] = [
    { id: 'profile',          label: 'My Profile',       sub: 'Personal info & org',       path: '/profile'          },
    { id: 'account-settings', label: 'Account Settings', sub: 'General account options',   path: '/account-settings' },
    { id: 'security',         label: 'Security',         sub: 'Password, 2FA & sessions',  path: '/security'         },
    { id: 'preferences',      label: 'Preferences',      sub: 'Theme, language & formats', path: '/preferences'      },
    { id: 'help',             label: 'Help & Support',   sub: 'Docs & keyboard shortcuts', path: '/help'             },
  ];
}
