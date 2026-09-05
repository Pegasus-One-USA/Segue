import { Observable } from 'rxjs';
import { Role, Permission, PermissionCategory } from '../../auth/models/user.model';
import { CreateRoleRequest, UpdateRoleRequest } from '../../auth/models/auth-request.model';

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface RoleFilter {
  search?: string;
  sortDescending?: boolean;
  page: number;
  pageSize: number;
}

export abstract class IRoleService {
  abstract getRoles(): Observable<Role[]>;
  abstract getPagedRoles(filter: RoleFilter): Observable<PagedResult<Role>>;
  abstract getRole(id: string): Observable<Role>;
  abstract createRole(req: CreateRoleRequest): Observable<Role>;
  abstract updateRole(id: string, req: UpdateRoleRequest): Observable<Role>;
  abstract deleteRole(id: string): Observable<void>;
  abstract getPermissions(): Observable<Permission[]>;
  abstract getPermissionCatalog(): Observable<PermissionCategory[]>;
}
