// user-management/dialogs/edit-user-dialog/edit-user-dialog.component.ts
import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';

import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';

import { ToastService } from '../../../services/toast.service';
import { IUserService } from '../../../auth/services/i-user.service';
import { User, UserRole, UserStatus } from '../../../auth/models/user.model';
import { UpdateUserRequest } from '../../../auth/models/auth-request.model';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

interface DialogData {
  user: User;
}

const ROLE_OPTIONS: { value: UserRole; label: string }[] = [
  { value: 'SuperAdmin', label: 'Super Admin' },
  { value: 'Admin',      label: 'Admin' },
  { value: 'Operations', label: 'Operations' },
  { value: 'Audit',      label: 'Audit' },
];

const STATUS_OPTIONS: { value: UserStatus; label: string }[] = [
  { value: 'active',    label: 'Active' },
  { value: 'inactive',  label: 'Deactivated' },
  { value: 'pending',   label: 'Pending' },
  { value: 'suspended', label: 'Suspended' },
];

@Component({
  selector: 'app-edit-user-dialog',
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
  templateUrl: './edit-user-dialog.component.html',
  styleUrls: ['./edit-user-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class EditUserDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  readonly dialogRef           = inject<DialogRef<User | null>>(DialogRef);
  private readonly toast       = inject(ToastService);
  private readonly fb          = inject(FormBuilder);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly data                = inject<DialogData>(DIALOG_DATA);

  // ─── State ───────────────────────────────────────────────────────────────
  loading   = signal(false);
  submitted = signal(false);

  // ─── Form ────────────────────────────────────────────────────────────────
  form!: FormGroup;

  readonly roleOptions   = ROLE_OPTIONS;
  readonly statusOptions = STATUS_OPTIONS;

  // ─── Lifecycle ───────────────────────────────────────────────────────────
  ngOnInit(): void {
    const u = this.data.user;
    this.form = this.fb.group({
      firstName:  [u.firstName,  [Validators.required, Validators.maxLength(80)]],
      lastName:   [u.lastName,   [Validators.required, Validators.maxLength(80)]],
      department: [u.department ?? ''],
      jobTitle:   [u.jobTitle   ?? ''],
      phone:      [u.phone      ?? ''],
      role:       [u.role,       Validators.required],
      status:     [u.status,     Validators.required],
    });
  }

  // ─── Helpers ─────────────────────────────────────────────────────────────
  hasError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl && ctrl.hasError(error) && (ctrl.touched || this.submitted()));
  }

  // ─── Submit ──────────────────────────────────────────────────────────────
  submit(): void {
    // Defense-in-depth: this dialog is now gated at every known trigger (user-list, user-detail) —
    // this re-check guards against a permission change landing in another tab while it's still open.
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Edit), 'You do not have permission to edit users.')) return;
    this.submitted.set(true);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    const value = this.form.value;
    const req: UpdateUserRequest = {
      firstName:  value.firstName.trim(),
      lastName:   value.lastName.trim(),
      department: value.department?.trim() || undefined,
      jobTitle:   value.jobTitle?.trim()   || undefined,
      phone:      value.phone?.trim()      || undefined,
      role:       value.role,
      status:     value.status,
    };

    this.userService.updateUser(this.data.user.id, req).subscribe({
      next: user => {
        this.loading.set(false);
        this.toast.success(`User "${user.fullName}" updated successfully.`);
        this.dialogRef.close(user);
      },
      error: err => {
        this.loading.set(false);
        this.toast.error(err?.message ?? 'Failed to update user. Please try again.');
      },
    });
  }

  isSaveInProgress(): boolean {
    return this.loading();
  }
}
