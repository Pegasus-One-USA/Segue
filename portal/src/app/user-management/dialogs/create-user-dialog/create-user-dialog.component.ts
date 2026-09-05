import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';

import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ToastService } from '../../../services/toast.service';
import { InvitationService } from '../../../auth/services/invitation.service';
import { TenantRoleService } from '../../services/tenant-role.service';
import { UserRole } from '../../../auth/models/user.model';
import { Invitation } from '../../../auth/models/invitation.model';
import { DialogRef } from '../../../core/services/dialog.service';

const ROLES: { value: UserRole; label: string }[] = [
  { value: 'SuperAdmin', label: 'Super Admin' },
  { value: 'Admin',      label: 'Admin' },
  { value: 'Operations', label: 'Operations' },
  { value: 'Audit',      label: 'Audit' },
];

@Component({
  selector: 'app-create-user-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './create-user-dialog.component.html',
  styleUrls: ['./create-user-dialog.component.scss'],
})
// No explicit attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) calls this
// component's own hasUnsavedChanges() override below (kept explicit, rather than relying on the
// generic `form` duck-typing, because "unsaved" here also depends on inviteSent() — once the
// invitation is sent there's nothing left to lose even if the form itself is still dirty).
export class CreateUserDialogComponent implements OnInit {
  private readonly invitationSvc = inject(InvitationService);
  readonly dialogRef             = inject<DialogRef<Invitation | null>>(DialogRef);
  private readonly toast         = inject(ToastService);
  private readonly fb            = inject(FormBuilder);

  readonly tenantRoleSvc = inject(TenantRoleService);

  loading       = signal(false);
  submitted     = signal(false);
  inviteSent    = signal(false);
  sentInvitation = signal<Invitation | null>(null);
  copied        = signal(false);
  form!: FormGroup;

  readonly availableRoles = ROLES;

  get activationUrl(): string {
    const inv = this.sentInvitation();
    if (!inv) return '';
    return this.invitationSvc.getActivationUrl(inv.token);
  }

  ngOnInit(): void {
    this.form = this.fb.group({
      firstName: ['', [Validators.required, Validators.maxLength(80)]],
      lastName:  ['', [Validators.required, Validators.maxLength(80)]],
      email:     ['', [Validators.required, Validators.email]],
      role:      ['Audit', Validators.required],
    });
  }

  hasError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl && ctrl.hasError(error) && (ctrl.touched || this.submitted()));
  }

  submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    const v = this.form.value;

    this.invitationSvc.sendInvitation({
      email:     v.email.trim().toLowerCase(),
      firstName: v.firstName.trim(),
      lastName:  v.lastName.trim(),
      role:      v.role,
      invitedBy: 'Admin',
    }).subscribe({
      next: invitation => {
        this.loading.set(false);
        this.sentInvitation.set(invitation);
        this.inviteSent.set(true);
      },
      error: err => {
        this.loading.set(false);
        this.toast.error(err?.message ?? 'Failed to send invitation. Please try again.');
      },
    });
  }

  copyLink(): void {
    navigator.clipboard.writeText(this.activationUrl).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2500);
    });
  }

  // Once the invitation is sent, there's nothing left to lose — the success screen's own "Done"-style
  // close (the header's × still routes through here) can just close immediately with the result.
  close(): void {
    this.dialogRef.close(this.inviteSent() ? this.sentInvitation() : null);
  }

  hasUnsavedChanges(): boolean {
    return !this.inviteSent() && this.form.dirty;
  }

  isSaveInProgress(): boolean {
    return this.loading();
  }
}
