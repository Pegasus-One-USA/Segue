import { Component, inject, signal } from '@angular/core';
import { Router }               from '@angular/router';
import { FormsModule }          from '@angular/forms';
import { SidebarComponent }     from '../../../dashboard/layout/sidebar/sidebar.component';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';
import { UserProfileService }   from '../../services/user-profile.service';

@Component({
  selector:    'app-account-settings',
  standalone:  true,
  imports:     [FormsModule, SidebarComponent, UserSettingsNavComponent],
  templateUrl: './account-settings.component.html',
  styleUrl:    './account-settings.component.scss',
})
export class AccountSettingsComponent {
  private readonly router  = inject(Router);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly sidebarCollapsed = signal(false);
  protected readonly saved            = signal(false);

  protected readonly profile   = this.profSvc.profile;
  protected readonly fullName  = this.profSvc.fullName;
  protected readonly initials  = this.profSvc.initials;

  protected firstName  = this.profile().firstName;
  protected lastName   = this.profile().lastName;
  protected phone      = this.profile().phone;
  protected department = this.profile().department;
  protected designation = this.profile().designation;
  protected location   = this.profile().location;

  toggleSidebar(): void { this.sidebarCollapsed.update(v => !v); }
  navigate(path: string): void { this.router.navigate([path]); }

  saveChanges(): void {
    this.profSvc.updateProfile({
      firstName: this.firstName,
      lastName:  this.lastName,
      phone:     this.phone,
      department: this.department,
      designation: this.designation,
      location:  this.location,
      avatarInitials: (this.firstName[0] ?? '') + (this.lastName[0] ?? ''),
    });
    this.saved.set(true);
    setTimeout(() => this.saved.set(false), 3000);
  }
}
