import { Component, inject, signal } from '@angular/core';
import { DatePipe }             from '@angular/common';
import { UserProfileService }   from '../../services/user-profile.service';

@Component({
  selector:    'app-security',
  standalone:  true,
  imports:     [DatePipe],
  templateUrl: './security.component.html',
  styleUrl:    './security.component.scss',
})
export class SecurityComponent {
  protected readonly profSvc = inject(UserProfileService);

  protected readonly pwChangeOpen = signal(false);
  protected readonly twoFaSetupOpen   = signal(false);

  protected readonly profile   = this.profSvc.profile;
  protected readonly sessions  = this.profSvc.sessions;
  protected readonly apiKeys   = this.profSvc.apiKeys;

  togglePwChange(): void   { this.pwChangeOpen.update(v => !v); }

  toggleTwoFactor(): void  { this.profSvc.toggleTwoFactor(); }

  revokeSession(id: string): void { this.profSvc.revokeSession(id); }
  revokeApiKey(id: string): void  { this.profSvc.revokeApiKey(id); }
}
