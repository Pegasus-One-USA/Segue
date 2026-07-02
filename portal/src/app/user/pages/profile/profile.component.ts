import { Component, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { UserProfileService } from '../../services/user-profile.service';
import { ROLE_DEFINITIONS } from '../../models/user-profile.model';
import { EditProfileDialogComponent } from '../../dialogs/edit-profile-dialog/edit-profile-dialog.component';

@Component({
  selector:    'app-profile',
  standalone:  true,
  imports:     [DatePipe],
  templateUrl: './profile.component.html',
  styleUrl:    './profile.component.scss',
})
export class ProfileComponent {
  private readonly dialog  = inject(MatDialog);
  private readonly snack   = inject(MatSnackBar);
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
      .open(EditProfileDialogComponent, {
        width: '520px',
        disableClose: true,
        restoreFocus: false,
        data: {
          firstName: p.firstName,
          lastName:  p.lastName,
          email:     p.email,
          phone:     p.phone,
        },
      })
      .afterClosed()
      .subscribe(saved => {
        if (saved) this.snack.open('Profile updated successfully.', 'Dismiss', { duration: 3000 });
      });
  }
}
