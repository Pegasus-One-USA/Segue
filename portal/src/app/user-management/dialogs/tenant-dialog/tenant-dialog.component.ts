import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TenantRoleService, Tenant } from '../../services/tenant-role.service';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

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
    MatProgressSpinnerModule,
  ],
  templateUrl: './tenant-dialog.component.html',
  styleUrls: ['./tenant-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class TenantDialogComponent {
  private readonly svc   = inject(TenantRoleService);
  private readonly fb    = inject(FormBuilder);
  readonly dialogRef     = inject<DialogRef<boolean>>(DialogRef);
  readonly data: TenantDialogData = inject(DIALOG_DATA) as TenantDialogData;

  readonly isEdit       = !!this.data?.tenant;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

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

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const { name, code } = this.form.value as { name: string; code: string };
    this.saving.set(true);

    const request$ = this.isEdit
      ? this.svc.updateTenant(this.data.tenant!.id, { name, code })
      : this.svc.addTenant({ name, code });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.message ?? 'Failed to save tenant. Please try again.');
      },
    });
  }
}
