import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { TenantRoleService, Tenant } from '../../services/tenant-role.service';

export interface TenantDialogData {
  tenant?: Tenant;
}

@Component({
  selector: 'app-tenant-dialog',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
  ],
  templateUrl: './tenant-dialog.component.html',
  styleUrls: ['./tenant-dialog.component.scss'],
})
export class TenantDialogComponent {
  private readonly svc   = inject(TenantRoleService);
  private readonly fb    = inject(FormBuilder);
  readonly dialogRef     = inject(MatDialogRef<TenantDialogComponent>);
  readonly data: TenantDialogData = inject(MAT_DIALOG_DATA);

  readonly isEdit    = !!this.data?.tenant;
  readonly submitted = signal(false);

  readonly form = this.fb.group({
    name: [this.data?.tenant?.name ?? '', [
      Validators.required,
      Validators.maxLength(120),
    ]],
    code: [this.data?.tenant?.code ?? '', [
      Validators.required,
      Validators.maxLength(20),
      Validators.pattern(/^[A-Za-z0-9_-]+$/),
    ]],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const { name, code } = this.form.value as { name: string; code: string };
    if (this.isEdit) {
      this.svc.updateTenant(this.data.tenant!.id, { name, code });
    } else {
      this.svc.addTenant({ name, code });
    }
    this.dialogRef.close(true);
  }
}
