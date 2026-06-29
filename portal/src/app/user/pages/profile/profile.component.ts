import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { DatePipe }             from '@angular/common';
import { SidebarComponent }     from '../../../dashboard/layout/sidebar/sidebar.component';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';
import { UserProfileService }   from '../../services/user-profile.service';
import { ROLE_DEFINITIONS }     from '../../models/user-profile.model';

@Component({
  selector:    'app-profile',
  standalone:  true,
  imports:     [DatePipe, SidebarComponent, UserSettingsNavComponent],
  templateUrl: './profile.component.html',
  styleUrl:    './profile.component.scss',
})
export class ProfileComponent {
  private readonly router  = inject(Router);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly editMode         = signal(false);

  protected readonly profile   = this.profSvc.profile;
  protected readonly fullName  = this.profSvc.fullName;
  protected readonly initials  = this.profSvc.initials;
  protected readonly roleDefs  = ROLE_DEFINITIONS;

  protected get currentRole() {
    return this.roleDefs.find(r => r.id === this.profile().role);
  }

  toggleSidebar(): void { this.sidebarCollapsed.update(v => !v); }

  navigate(path: string): void { this.router.navigate([path]); }
}
