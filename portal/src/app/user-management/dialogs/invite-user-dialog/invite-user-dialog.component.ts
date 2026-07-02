// user-management/dialogs/invite-user-dialog/invite-user-dialog.component.ts
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
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IUserService } from '../../../auth/services/i-user.service';
import { UserRole } from '../../../auth/models/user.model';
import { InviteUserRequest } from '../../../auth/models/auth-request.model';

const ROLE_OPTIONS: { value: UserRole; label: string }[] = [
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
  selector: 'app-invite-user-dialog',
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
    MatDividerModule,
  ],
  templateUrl: './invite-user-dialog.component.html',
  styleUrls: ['./invite-user-dialog.component.scss'],
})
export class InviteUserDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  private readonly dialogRef   = inject(MatDialogRef<InviteUserDialogComponent>);
  private readonly snackBar    = inject(MatSnackBar);
  private readonly fb          = inject(FormBuilder);

  // ─── State ───────────────────────────────────────────────────────────────
  loading   = signal(false);
  submitted = signal(false);

  // ─── Form ────────────────────────────────────────────────────────────────
  form!: FormGroup;

  readonly roleOptions = ROLE_OPTIONS;

  // ─── Lifecycle ───────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.form = this.fb.group({
      email:      ['', [Validators.required, Validators.email]],
      firstName:  ['', [Validators.required, Validators.maxLength(80)]],
      lastName:   ['', [Validators.required, Validators.maxLength(80)]],
      role:       ['viewer' as UserRole, Validators.required],
      department: [''],
    });
  }

  // ─── Helpers ─────────────────────────────────────────────────────────────
  hasError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl && ctrl.hasError(error) && (ctrl.touched || this.submitted()));
  }

  // ─── Submit ──────────────────────────────────────────────────────────────
  submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    const value = this.form.value;
    const req: InviteUserRequest = {
      email:      value.email.trim().toLowerCase(),
      firstName:  value.firstName.trim(),
      lastName:   value.lastName.trim(),
      role:       value.role,
      department: value.department?.trim() || undefined,
    };

    this.userService.inviteUser(req).subscribe({
      next: res => {
        this.loading.set(false);
        this.snackBar.open(
          res.message ?? `Invitation sent to "${req.email}".`,
          'Dismiss',
          { duration: 4000, panelClass: 'snack-success' },
        );
        this.dialogRef.close(res);
      },
      error: err => {
        this.loading.set(false);
        this.snackBar.open(
          err?.message ?? 'Failed to send invitation. Please try again.',
          'Dismiss',
          { duration: 5000, panelClass: 'snack-error' },
        );
      },
    });
  }

  cancel(): void {
    this.dialogRef.close(null);
  }
}
