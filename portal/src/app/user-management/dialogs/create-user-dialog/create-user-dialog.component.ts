// user-management/dialogs/create-user-dialog/create-user-dialog.component.ts
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
import { TenantRoleService, CustomRole } from '../../services/tenant-role.service';
import { CreateUserRequest } from '../../../auth/models/auth-request.model';

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
    MatDividerModule,
  ],
  templateUrl: './create-user-dialog.component.html',
  styleUrls: ['./create-user-dialog.component.scss'],
})
export class CreateUserDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  private readonly dialogRef   = inject(MatDialogRef<CreateUserDialogComponent>);
  private readonly snackBar    = inject(MatSnackBar);
  private readonly fb          = inject(FormBuilder);
  readonly tenantRoleSvc       = inject(TenantRoleService);

  loading   = signal(false);
  submitted = signal(false);
  form!: FormGroup;

  get tenants() { return this.tenantRoleSvc.tenants(); }

  rolesForSelectedTenant(): CustomRole[] {
    const tenantId = this.form?.get('tenantId')?.value as string;
    return tenantId
      ? this.tenantRoleSvc.roles().filter(r => r.tenantId === tenantId)
      : this.tenantRoleSvc.roles();
  }

  ngOnInit(): void {
    this.form = this.fb.group({
      firstName:    ['', [Validators.required, Validators.maxLength(80)]],
      lastName:     ['', [Validators.required, Validators.maxLength(80)]],
      email:        ['', [Validators.required, Validators.email]],
      tenantId:     ['', Validators.required],
      customRoleId: ['', Validators.required],
    });

    this.form.get('tenantId')?.valueChanges.subscribe(() => {
      this.form.get('customRoleId')?.reset('');
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
    const req: CreateUserRequest = {
      firstName:  v.firstName.trim(),
      lastName:   v.lastName.trim(),
      email:      v.email.trim().toLowerCase(),
      role:       'viewer',
      loginType:  'local',
      status:     'active',
      sendInvite: false,
    };

    this.userService.createUser(req).subscribe({
      next: user => {
        this.loading.set(false);
        this.snackBar.open(
          `User "${user.fullName}" created successfully.`,
          'Dismiss',
          { duration: 4000, panelClass: 'snack-success' },
        );
        this.dialogRef.close(user);
      },
      error: err => {
        this.loading.set(false);
        this.snackBar.open(
          err?.message ?? 'Failed to create user. Please try again.',
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
