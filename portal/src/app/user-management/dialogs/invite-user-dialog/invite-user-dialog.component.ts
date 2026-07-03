// user-management/dialogs/invite-user-dialog/invite-user-dialog.component.ts
import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';

import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar } from '@angular/material/snack-bar';

import { IUserService } from '../../../auth/services/i-user.service';
import { Role, UserRole } from '../../../auth/models/user.model';
import { InviteUserRequest } from '../../../auth/models/auth-request.model';
import { InviteResultDialogComponent } from '../invite-result-dialog/invite-result-dialog.component';

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
  private readonly dialog      = inject(MatDialog);
  private readonly snackBar    = inject(MatSnackBar);
  private readonly fb          = inject(FormBuilder);

  // ─── State ───────────────────────────────────────────────────────────────
  loading      = signal(false);
  submitted    = signal(false);
  rolesLoading = signal(true);
  roles        = signal<Role[]>([]);

  // ─── Form ────────────────────────────────────────────────────────────────
  form!: FormGroup;

  // ─── Lifecycle ───────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.form = this.fb.group({
      email:     ['', [Validators.required, Validators.email]],
      firstName: ['', [Validators.maxLength(80)]],
      lastName:  ['', [Validators.maxLength(80)]],
      roleId:    ['', Validators.required],
    });

    this.loadRoles();
  }

  private loadRoles(): void {
    this.rolesLoading.set(true);
    this.userService.getRoles().subscribe({
      next: roles => {
        this.roles.set(roles);
        this.rolesLoading.set(false);
        // Default to a sensible non-admin role if present.
        const defaultRole = roles.find(r => r.name === 'Audit') ?? roles[0];
        if (defaultRole) this.form.get('roleId')!.setValue(defaultRole.id);
      },
      error: err => {
        this.rolesLoading.set(false);
        this.snackBar.open(err?.message ?? 'Failed to load roles.', 'Dismiss', { duration: 4000 });
      },
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
    const selectedRole = this.roles().find(r => r.id === value.roleId);
    const req: InviteUserRequest = {
      email:     value.email.trim().toLowerCase(),
      firstName: value.firstName?.trim() ?? '',
      lastName:  value.lastName?.trim() ?? '',
      role:      (selectedRole?.name ?? 'Audit') as UserRole,
      roleId:    value.roleId,
    };

    this.userService.inviteUser(req).subscribe({
      next: res => {
        this.loading.set(false);
        this.snackBar.open(
          res.message ?? `Invitation sent to "${req.email}".`,
          'Dismiss',
          { duration: 4000, panelClass: 'snack-success' },
        );
        // Show the invitation link with a copy button.
        this.dialog.open(InviteResultDialogComponent, {
          width: '540px', restoreFocus: false, data: res,
        });
        this.dialogRef.close(res);
      },
      error: err => {
        this.loading.set(false);
        this.snackBar.open(
          err?.error?.message ?? err?.message ?? 'Failed to send invitation. Please try again.',
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
