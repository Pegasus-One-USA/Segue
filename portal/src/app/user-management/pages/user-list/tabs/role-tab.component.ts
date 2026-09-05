import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';

import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { TenantRoleService, CustomRole } from '../../../services/tenant-role.service';

@Component({
  selector: 'app-role-tab',
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
  templateUrl: './role-tab.component.html',
  styleUrls: ['./role-tab.component.scss'],
})
export class RoleTabComponent {
  private readonly svc = inject(TenantRoleService);
  private readonly fb  = inject(FormBuilder);

  readonly tenants       = this.svc.tenants;
  readonly roles         = this.svc.roles;
  readonly displayedCols = ['name', 'description', 'tenant', 'createdAt', 'actions'];

  editId    = signal<string | null>(null);
  submitted = signal(false);

  form = this.fb.group({
    name:        ['', [Validators.required, Validators.maxLength(120)]],
    description: ['', [Validators.required, Validators.maxLength(300)]],
    tenantId:    ['', Validators.required],
  });

  hasError(ctrl: string, error: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(error) && (c.touched || this.submitted());
  }

  submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const { name, description, tenantId } =
      this.form.value as { name: string; description: string; tenantId: string };

    if (this.editId()) {
      this.svc.updateRole(this.editId()!, { name, description, tenantId });
      this.editId.set(null);
    } else {
      this.svc.addRole({ name, description, tenantId });
    }
    this.form.reset({ name: '', description: '', tenantId: '' });
    this.submitted.set(false);
  }

  startEdit(role: CustomRole): void {
    this.editId.set(role.id);
    this.form.setValue({ name: role.name, description: role.description, tenantId: role.tenantId });
    this.submitted.set(false);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  cancelEdit(): void {
    this.editId.set(null);
    this.form.reset({ name: '', description: '', tenantId: '' });
    this.submitted.set(false);
  }

  deleteRole(role: CustomRole): void {
    if (!confirm(`Delete role "${role.name}"?`)) return;
    this.svc.deleteRole(role.id);
    if (this.editId() === role.id) this.cancelEdit();
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString('en-US', {
      year: 'numeric', month: 'short', day: 'numeric',
    });
  }
}
