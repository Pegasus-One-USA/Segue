import { Observable } from 'rxjs';
import { Role, Permission, PermissionCategory } from '../../auth/models/user.model';
import { CreateRoleRequest, UpdateRoleRequest } from '../../auth/models/auth-request.model';

export abstract class IRoleService {
  abstract getRoles(): Observable<Role[]>;
  abstract getRole(id: string): Observable<Role>;
  abstract createRole(req: CreateRoleRequest): Observable<Role>;
  abstract updateRole(id: string, req: UpdateRoleRequest): Observable<Role>;
  abstract deleteRole(id: string): Observable<void>;
  abstract getPermissions(): Observable<Permission[]>;
  abstract getPermissionCatalog(): Observable<PermissionCategory[]>;
}
