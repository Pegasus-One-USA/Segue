import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';

import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { InvitationService } from '../../../auth/services/invitation.service';
import { TenantRoleService } from '../../services/tenant-role.service';
import { UserRole } from '../../../auth/models/user.model';
import { Invitation } from '../../../auth/models/invitation.model';

const ROLES: { value: UserRole; label: string }[] = [
  { value: 'system-admin',    label: 'System Admin' },
  { value: 'tenant-admin',    label: 'Tenant Admin' },
  { value: 'developer',       label: 'Developer' },
  { value: 'pipeline-editor', label: 'Pipeline Editor' },
  { value: 'reviewer',        label: 'Reviewer' },
  { value: 'auditor',         label: 'Auditor' },
  { value: 'analyst',         label: 'Analyst' },
  { value: 'viewer',          label: 'Viewer' },
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
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './create-user-dialog.component.html',
  styleUrls: ['./create-user-dialog.component.scss'],
})
export class CreateUserDialogComponent implements OnInit {
  private readonly invitationSvc = inject(InvitationService);
  private readonly dialogRef     = inject(MatDialogRef<CreateUserDialogComponent>);
  private readonly snackBar      = inject(MatSnackBar);
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
      role:      ['viewer', Validators.required],
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
        this.snackBar.open(
          err?.message ?? 'Failed to send invitation. Please try again.',
          'Dismiss',
          { duration: 5000 },
        );
      },
    });
  }

  copyLink(): void {
    navigator.clipboard.writeText(this.activationUrl).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2500);
    });
  }

  close(): void {
    this.dialogRef.close(this.inviteSent() ? this.sentInvitation() : null);
  }

  cancel(): void {
    this.dialogRef.close(null);
  }
}
