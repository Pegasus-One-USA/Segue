import { Injectable, signal } from '@angular/core';

export interface Tenant {
  id: string;
  name: string;
  code: string;
  createdAt: string;
}

export interface CustomRole {
  id: string;
  name: string;
  description: string;
  tenantId: string;
  tenantName: string;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class TenantRoleService {
  readonly tenants = signal<Tenant[]>([]);
  readonly roles   = signal<CustomRole[]>([]);

  addTenant(data: Pick<Tenant, 'name' | 'code'>): Tenant {
    const tenant: Tenant = {
      id:        this._uid(),
      name:      data.name.trim(),
      code:      data.code.trim().toUpperCase(),
      createdAt: new Date().toISOString(),
    };
    this.tenants.update(list => [...list, tenant]);
    return tenant;
  }

  updateTenant(id: string, data: Pick<Tenant, 'name' | 'code'>): void {
    this.tenants.update(list =>
      list.map(t => t.id === id
        ? { ...t, name: data.name.trim(), code: data.code.trim().toUpperCase() }
        : t));
    this.roles.update(list =>
      list.map(r => r.tenantId === id ? { ...r, tenantName: data.name.trim() } : r));
  }

  deleteTenant(id: string): void {
    this.tenants.update(list => list.filter(t => t.id !== id));
    this.roles.update(list => list.filter(r => r.tenantId !== id));
  }

  addRole(data: Pick<CustomRole, 'name' | 'description' | 'tenantId'>): CustomRole {
    const tenant = this.tenants().find(t => t.id === data.tenantId);
    const role: CustomRole = {
      id:          this._uid(),
      name:        data.name.trim(),
      description: data.description.trim(),
      tenantId:    data.tenantId,
      tenantName:  tenant?.name ?? '',
      createdAt:   new Date().toISOString(),
    };
    this.roles.update(list => [...list, role]);
    return role;
  }

  updateRole(id: string, data: Pick<CustomRole, 'name' | 'description' | 'tenantId'>): void {
    const tenant = this.tenants().find(t => t.id === data.tenantId);
    this.roles.update(list =>
      list.map(r => r.id === id ? {
        ...r,
        name:        data.name.trim(),
        description: data.description.trim(),
        tenantId:    data.tenantId,
        tenantName:  tenant?.name ?? '',
      } : r));
  }

  deleteRole(id: string): void {
    this.roles.update(list => list.filter(r => r.id !== id));
  }

  rolesForTenant(tenantId: string): CustomRole[] {
    return tenantId
      ? this.roles().filter(r => r.tenantId === tenantId)
      : this.roles();
  }

  private _uid(): string {
    return Date.now().toString(36) + Math.random().toString(36).slice(2, 9);
  }
}
