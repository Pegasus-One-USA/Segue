import { Observable } from 'rxjs';
import { Role, Permission, PermissionCategory } from '../../auth/models/user.model';
import { CreateRoleRequest, UpdateRoleRequest } from '../../auth/models/auth-request.model';
import { NodeCatalogEntry } from '../../models/node-catalog.model';

export abstract class IRoleService {
  abstract getRoles(): Observable<Role[]>;
  abstract getRole(id: string): Observable<Role>;
  abstract createRole(req: CreateRoleRequest): Observable<Role>;
  abstract updateRole(id: string, req: UpdateRoleRequest): Observable<Role>;
  abstract deleteRole(id: string): Observable<void>;
  abstract getPermissions(): Observable<Permission[]>;
  abstract getPermissionCatalog(): Observable<PermissionCategory[]>;
  /** The canonical Node Catalog — the same data GET /api/v1/permissions/node-catalog returns to the
   *  Workflow Builder Node Library. See NodeCatalogService for the app-wide cached version of this;
   *  this one is unfiltered/uncached, matching getPermissionCatalog()'s own pattern above. */
  abstract getNodeCatalog(): Observable<NodeCatalogEntry[]>;
}
