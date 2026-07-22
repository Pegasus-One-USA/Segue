import { Component, inject, signal } from '@angular/core';
import { Router }               from '@angular/router';
import { FormsModule }          from '@angular/forms';
import { UserSettingsNavComponent } from '../../components/user-settings-nav/user-settings-nav.component';
import { UserProfileService }   from '../../services/user-profile.service';
import { HasUnsavedChanges }    from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';

@Component({
  selector:    'app-account-settings',
  standalone:  true,
  imports:     [FormsModule, UserSettingsNavComponent],
  templateUrl: './account-settings.component.html',
  styleUrl:    './account-settings.component.scss',
})
export class AccountSettingsComponent implements HasUnsavedChanges {
  private readonly router  = inject(Router);
  protected readonly profSvc = inject(UserProfileService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  protected readonly saved = signal(false);

  constructor() {
    this.unsavedChangesRegistry.register(() => this.hasUnsavedChanges());
  }

  protected readonly profile   = this.profSvc.profile;
  protected readonly fullName  = this.profSvc.fullName;
  protected readonly initials  = this.profSvc.initials;

  protected firstName  = this.profile().firstName;
  protected lastName   = this.profile().lastName;
  protected phone      = this.profile().phone;
  protected department = this.profile().department;
  protected designation = this.profile().designation;
  protected location   = this.profile().location;

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

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    const p = this.profile();
    return this.firstName !== p.firstName
      || this.lastName !== p.lastName
      || this.phone !== p.phone
      || this.department !== p.department
      || this.designation !== p.designation
      || this.location !== p.location;
  }
}
