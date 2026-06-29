import { Component, inject, signal } from '@angular/core';
import { Router }               from '@angular/router';
import { DatePipe }             from '@angular/common';
import { SidebarComponent }     from '../../../dashboard/layout/sidebar/sidebar.component';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';
import { UserProfileService }   from '../../services/user-profile.service';

@Component({
  selector:    'app-security',
  standalone:  true,
  imports:     [DatePipe, SidebarComponent, UserSettingsNavComponent],
  templateUrl: './security.component.html',
  styleUrl:    './security.component.scss',
})
export class SecurityComponent {
  private readonly router  = inject(Router);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly pwChangeOpen     = signal(false);
  protected readonly twoFaSetupOpen   = signal(false);

  protected readonly profile   = this.profSvc.profile;
  protected readonly sessions  = this.profSvc.sessions;
  protected readonly apiKeys   = this.profSvc.apiKeys;

  toggleSidebar(): void    { this.sidebarCollapsed.update(v => !v); }
  navigate(path: string): void { this.router.navigate([path]); }
  togglePwChange(): void   { this.pwChangeOpen.update(v => !v); }

  toggleTwoFactor(): void  { this.profSvc.toggleTwoFactor(); }

  revokeSession(id: string): void { this.profSvc.revokeSession(id); }
  revokeApiKey(id: string): void  { this.profSvc.revokeApiKey(id); }
}
