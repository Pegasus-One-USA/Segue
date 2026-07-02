import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TenantRoleService, CustomRole } from '../../services/tenant-role.service';

export interface RoleDialogData {
  role?: CustomRole;
}

@Component({
  selector: 'app-role-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
  ],
  templateUrl: './role-dialog.component.html',
  styleUrls: ['./role-dialog.component.scss'],
})
export class RoleDialogComponent {
  private readonly svc   = inject(TenantRoleService);
  private readonly fb    = inject(FormBuilder);
  readonly dialogRef     = inject(MatDialogRef<RoleDialogComponent>);
  readonly data: RoleDialogData = inject(MAT_DIALOG_DATA);

  readonly isEdit    = !!this.data?.role;
  readonly submitted = signal(false);
  readonly tenants   = this.svc.tenants;

  readonly form = this.fb.group({
    name: [this.data?.role?.name ?? '', [
      Validators.required,
      Validators.maxLength(120),
    ]],
    description: [this.data?.role?.description ?? '', [
      Validators.required,
      Validators.maxLength(300),
    ]],
    tenantId: [this.data?.role?.tenantId ?? '', Validators.required],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const { name, description, tenantId } =
      this.form.value as { name: string; description: string; tenantId: string };
    if (this.isEdit) {
      this.svc.updateRole(this.data.role!.id, { name, description, tenantId });
    } else {
      this.svc.addRole({ name, description, tenantId });
    }
    this.dialogRef.close(true);
  }
}
