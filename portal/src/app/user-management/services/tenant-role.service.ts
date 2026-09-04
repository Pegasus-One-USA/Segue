import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, of, map, tap, catchError } from 'rxjs';
import { TENANT_ENDPOINTS } from '../../core/api-endpoints';

export interface Tenant {
  id: string;
  name: string;
  code: string;
  isActive: boolean;
  createdAt: string;
}

/** Wire shape of GET/POST/PUT /api/v1/tenants — mirrors the backend's TenantDto field-for-field. */
interface TenantDto {
  id: string;
  name: string;
  code: string;
  isActive: boolean;
  createdOnUtc: string;
  createdBy: string | null;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface CustomRole {
  id: string;
  name: string;
  description: string;
  tenantId: string;
  tenantName: string;
  createdAt: string;
}

/**
 * Tenant CRUD — the real backend for the Tenant Management screens (tenant-list, tenant-dialog,
 * tenant-tab), via TenantsController. Previously entirely an in-memory, non-persistent mock (a plain
 * signal<Tenant[]>([]) with no HttpClient and no localStorage) — every tenant created through this screen
 * used to vanish on the next page refresh; the database is now the source of truth.
 *
 * The `roles`/`CustomRole` half below (tenant-scoped "custom roles") is UNCHANGED and still an in-memory
 * mock — it doesn't correspond to anything in the real RBAC system (the real Role entity has no tenant
 * scoping at all) and wiring it up is a separate, unrelated feature from Tenant CRUD; only role-tab.component.ts
 * uses it, and that screen is left exactly as it was.
 */
@Injectable({ providedIn: 'root' })
export class TenantRoleService {
  private readonly http = inject(HttpClient);

  readonly tenants = signal<Tenant[]>([]);
  readonly roles   = signal<CustomRole[]>([]);

  constructor() {
    this.refreshTenants().subscribe();
  }

  /** Re-fetches the tenant list from the backend and refreshes the `tenants` signal. Called once eagerly
   *  at construction; callers that need to react to a failed initial load can also call this directly. */
  refreshTenants(): Observable<Tenant[]> {
    return this.http.get<TenantDto[]>(TENANT_ENDPOINTS.list).pipe(
      map(dtos => dtos.map(dto => this.fromDto(dto))),
      tap(list => this.tenants.set(list)),
      catchError(() => of(this.tenants())),
    );
  }

  /** Paged/search listing for the Tenant Management table (tenant-list.component.ts). Kept separate from the
   *  `tenants` signal above, which stays a full unpaged list because pickers elsewhere (create-user-dialog,
   *  role-tab, tenant-tab) need every tenant to populate a dropdown, not one page of it. */
  getPagedTenants(search: string | undefined, page: number, pageSize: number): Observable<PagedResult<Tenant>> {
    let params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));
    if (search) params = params.set('search', search);

    return this.http.get<PagedResult<TenantDto>>(TENANT_ENDPOINTS.paged, { params }).pipe(
      map(result => ({ ...result, items: result.items.map(dto => this.fromDto(dto)) })),
    );
  }

  addTenant(data: Pick<Tenant, 'name' | 'code'>): Observable<Tenant> {
    return this.http.post<TenantDto>(TENANT_ENDPOINTS.create, { name: data.name, code: data.code }).pipe(
      map(dto => this.fromDto(dto)),
      tap(tenant => this.tenants.update(list => [...list, tenant])),
    );
  }

  updateTenant(id: string, data: Pick<Tenant, 'name' | 'code'>): Observable<Tenant> {
    // The existing dialog/tab UI has no IsActive toggle — resend whatever this tenant's current value
    // already is so a plain rename never silently flips it.
    const isActive = this.tenants().find(t => t.id === id)?.isActive ?? true;
    return this.http.put<TenantDto>(TENANT_ENDPOINTS.update(id), { name: data.name, code: data.code, isActive }).pipe(
      map(dto => this.fromDto(dto)),
      tap(updated => this.tenants.update(list => list.map(t => t.id === id ? updated : t))),
    );
  }

  deleteTenant(id: string): Observable<void> {
    return this.http.delete<void>(TENANT_ENDPOINTS.delete(id)).pipe(
      tap(() => this.tenants.update(list => list.filter(t => t.id !== id))),
    );
  }

  private fromDto(dto: TenantDto): Tenant {
    return { id: dto.id, name: dto.name, code: dto.code, isActive: dto.isActive, createdAt: dto.createdOnUtc };
  }

  // ── Custom roles (mock, unchanged) ──────────────────────────────────────────
  // See the class doc comment — this half is intentionally untouched.

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
