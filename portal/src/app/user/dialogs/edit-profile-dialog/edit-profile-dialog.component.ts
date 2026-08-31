import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { UserProfileService } from '../../services/user-profile.service';
import { UserProfile } from '../../models/user-profile.model';

@Component({
  selector: 'app-edit-profile-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
  ],
  templateUrl: './edit-profile-dialog.component.html',
  styleUrls: ['./edit-profile-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically,
// so every plain single-reactive-form dialog gets "Discard changes?" gating for free with zero
// boilerplate.
export class EditProfileDialogComponent {
  private readonly profSvc = inject(UserProfileService);
  private readonly fb      = inject(FormBuilder);
  readonly dialogRef       = inject<DialogRef<boolean>>(DialogRef);
  readonly data: Partial<UserProfile> = inject(DIALOG_DATA) as Partial<UserProfile>;

  readonly submitted = signal(false);

  readonly form = this.fb.group({
    firstName: [this.data.firstName ?? '', [Validators.required, Validators.maxLength(80)]],
    lastName:  [this.data.lastName  ?? '', [Validators.required, Validators.maxLength(80)]],
    email:     [this.data.email     ?? '', [Validators.required, Validators.email]],
    phone:     [this.data.phone     ?? ''],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const { firstName, lastName, email, phone } =
      this.form.value as { firstName: string; lastName: string; email: string; phone: string };
    this.profSvc.updateProfile({ firstName, lastName, email, phone });
    this.dialogRef.close(true);
  }
}
