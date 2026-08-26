import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TenantRoleService, Tenant } from '../../../services/tenant-role.service';
import { ToastService } from '../../../../services/toast.service';

@Component({
  selector: 'app-tenant-tab',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './tenant-tab.component.html',
  styleUrls: ['./tenant-tab.component.scss'],
})
export class TenantTabComponent {
  private readonly svc   = inject(TenantRoleService);
  private readonly fb    = inject(FormBuilder);
  private readonly toast = inject(ToastService);

  readonly tenants       = this.svc.tenants;
  readonly displayedCols = ['name', 'code', 'createdAt', 'actions'];

  editId    = signal<string | null>(null);
  submitted = signal(false);
  saving    = signal(false);

  form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    code: ['', [Validators.required, Validators.maxLength(20),
                Validators.pattern(/^[A-Za-z0-9_-]+$/)]],
  });

  hasError(ctrl: string, error: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(error) && (c.touched || this.submitted());
  }

  submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const { name, code } = this.form.value as { name: string; code: string };
    this.saving.set(true);

    const editingId = this.editId();
    const request$ = editingId
      ? this.svc.updateTenant(editingId, { name, code })
      : this.svc.addTenant({ name, code });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.editId.set(null);
        this.form.reset({ name: '', code: '' });
        this.submitted.set(false);
        this.toast.success(editingId ? 'Tenant updated.' : 'Tenant created.');
      },
      error: (err) => {
        this.saving.set(false);
        this.toast.error('Save failed', err?.error?.message ?? 'Could not save tenant. Please try again.');
      },
    });
  }

  startEdit(tenant: Tenant): void {
    this.editId.set(tenant.id);
    this.form.setValue({ name: tenant.name, code: tenant.code });
    this.submitted.set(false);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  cancelEdit(): void {
    this.editId.set(null);
    this.form.reset({ name: '', code: '' });
    this.submitted.set(false);
  }

  deleteTenant(tenant: Tenant): void {
    if (!confirm(`Delete "${tenant.name}"?`)) return;
    this.svc.deleteTenant(tenant.id).subscribe({
      next: () => {
        if (this.editId() === tenant.id) this.cancelEdit();
        this.toast.success(`Tenant "${tenant.name}" deleted.`);
      },
      error: (err) => this.toast.error(
        'Delete failed', err?.error?.message ?? `Could not delete tenant "${tenant.name}".`),
    });
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
