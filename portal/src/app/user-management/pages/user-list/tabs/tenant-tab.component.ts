import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { TenantRoleService, Tenant } from '../../../services/tenant-role.service';

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
  ],
  templateUrl: './tenant-tab.component.html',
  styleUrls: ['./tenant-tab.component.scss'],
})
export class TenantTabComponent {
  private readonly svc = inject(TenantRoleService);
  private readonly fb  = inject(FormBuilder);

  readonly tenants       = this.svc.tenants;
  readonly displayedCols = ['name', 'code', 'createdAt', 'actions'];

  editId    = signal<string | null>(null);
  submitted = signal(false);

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

    if (this.editId()) {
      this.svc.updateTenant(this.editId()!, { name, code });
      this.editId.set(null);
    } else {
      this.svc.addTenant({ name, code });
    }
    this.form.reset({ name: '', code: '' });
    this.submitted.set(false);
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
    if (!confirm(`Delete "${tenant.name}"? All associated roles will also be removed.`)) return;
    this.svc.deleteTenant(tenant.id);
    if (this.editId() === tenant.id) this.cancelEdit();
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
