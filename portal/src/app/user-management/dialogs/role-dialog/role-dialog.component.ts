import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IRoleService } from '../../services/i-role.service';
import { Role } from '../../../auth/models/user.model';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';

export interface RoleDialogData {
  role?: Role;
}

@Component({
  selector: 'app-role-dialog',
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
  templateUrl: './role-dialog.component.html',
  styleUrls: ['./role-dialog.component.scss'],
})
export class RoleDialogComponent {
  private readonly svc         = inject(IRoleService);
  private readonly fb          = inject(FormBuilder);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly dialogRef   = inject(MatDialogRef<RoleDialogComponent>);
  readonly data: RoleDialogData = inject(MAT_DIALOG_DATA);

  readonly isEdit       = !!this.data?.role;
  readonly isSystemRole = !!this.data?.role?.isSystemRole;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    name: [
      { value: this.data?.role?.displayName ?? '', disabled: this.isSystemRole },
      [Validators.required, Validators.maxLength(120)],
    ],
    description: [
      { value: this.data?.role?.description ?? '', disabled: this.isSystemRole },
      [Validators.required, Validators.maxLength(300)],
    ],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    if (this.isSystemRole) return;
    // Defense-in-depth: the only way to reach this dialog today is via RoleListComponent's
    // openAdd()/openEdit(), which already gate on the same codes and hide their triggering buttons
    // entirely without permission — this re-check guards against a permission change landing in
    // another tab while this dialog is still open, not against a normally-reachable gap.
    const requiredCode = this.isEdit
      ? permissionCode(PermissionGroup.Role, PermissionAction.Edit)
      : permissionCode(PermissionGroup.Role, PermissionAction.Create);
    if (!this.actionGuard.ensure(requiredCode, `You do not have permission to ${this.isEdit ? 'edit' : 'create'} roles.`)) return;
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const { name, description } = this.form.getRawValue() as { name: string; description: string };
    this.saving.set(true);

    // updateRole replaces the role's full permission set, so name/description-only edits here
    // must resend the role's current permissions unchanged — the Permissions screen owns those.
    const permissionIds = this.data?.role?.permissions?.map(p => p.id) ?? [];

    const request$ = this.isEdit
      ? this.svc.updateRole(this.data.role!.id, { name, description, permissionIds })
      : this.svc.createRole({ name, description, permissionIds: [] });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save role. Please try again.');
      },
    });
  }
}
