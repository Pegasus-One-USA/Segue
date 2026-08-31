import { Component, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DialogService } from '../../../core/services/dialog.service';
import { UserProfileService } from '../../services/user-profile.service';
import { ToastService } from '../../../services/toast.service';
import { ROLE_DEFINITIONS, UserProfile } from '../../models/user-profile.model';
import { EditProfileDialogComponent } from '../../dialogs/edit-profile-dialog/edit-profile-dialog.component';

@Component({
  selector:    'app-profile',
  standalone:  true,
  imports:     [DatePipe],
  templateUrl: './profile.component.html',
  styleUrl:    './profile.component.scss',
})
export class ProfileComponent {
  private readonly dialog  = inject(DialogService);
  private readonly toast   = inject(ToastService);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly profile  = this.profSvc.profile;
  protected readonly fullName = this.profSvc.fullName;
  protected readonly initials = this.profSvc.initials;
  protected readonly roleDefs = ROLE_DEFINITIONS;

  protected get currentRole() {
    return this.roleDefs.find(r => r.id === this.profile().role);
  }

  openEditDialog(): void {
    const p = this.profile();
    this.dialog
      .open<EditProfileDialogComponent, Partial<UserProfile>, boolean>(EditProfileDialogComponent, {
        width: '520px',
        disableClose: true,
        data: {
          firstName: p.firstName,
          lastName:  p.lastName,
          email:     p.email,
          phone:     p.phone,
        },
      })
      .afterClosed()
      .subscribe(saved => {
        if (saved) this.toast.success('Profile updated successfully.');
      });
  }
}
