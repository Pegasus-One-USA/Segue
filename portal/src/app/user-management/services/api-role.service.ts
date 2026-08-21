import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { map, catchError } from 'rxjs/operators';
import { ROLES_ENDPOINTS, PERMISSIONS_ENDPOINTS } from '../../core/api-endpoints';
import { IRoleService } from './i-role.service';
import { mapRoleDto, mapPermissionDto, mapPermissionCatalogDto } from '../../auth/services/api-user.service';
import { CreateRoleRequest, UpdateRoleRequest } from '../../auth/models/auth-request.model';
import {
  Role, Permission, RoleDto, PermissionDto, PermissionCategory, PermissionCatalogCategoryDto,
} from '../../auth/models/user.model';
import { NodeCatalogEntry, NodeCatalogEntryDto, mapNodeCatalogEntryDto } from '../../models/node-catalog.model';

@Injectable({ providedIn: 'root' })
export class ApiRoleService extends IRoleService {
  private readonly http = inject(HttpClient);

  getRoles(): Observable<Role[]> {
    return this.http.get<RoleDto[]>(ROLES_ENDPOINTS.list).pipe(
      map(dtos => (dtos ?? []).map(mapRoleDto)),
      catchError(err => throwError(() => err))
    );
  }

  getRole(id: string): Observable<Role> {
    return this.http.get<RoleDto>(ROLES_ENDPOINTS.byId(id)).pipe(
      map(mapRoleDto),
      catchError(err => throwError(() => err))
    );
  }

  createRole(req: CreateRoleRequest): Observable<Role> {
    return this.http.post<RoleDto>(ROLES_ENDPOINTS.list, req).pipe(
      map(mapRoleDto),
      catchError(err => throwError(() => err))
    );
  }

  updateRole(id: string, req: UpdateRoleRequest): Observable<Role> {
    return this.http.put<RoleDto>(ROLES_ENDPOINTS.byId(id), req).pipe(
      map(mapRoleDto),
      catchError(err => throwError(() => err))
    );
  }

  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(ROLES_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  getPermissions(): Observable<Permission[]> {
    return this.http.get<PermissionDto[]>(PERMISSIONS_ENDPOINTS.list).pipe(
      map(dtos => (dtos ?? []).map(mapPermissionDto)),
      catchError(err => throwError(() => err))
    );
  }

  getPermissionCatalog(): Observable<PermissionCategory[]> {
    return this.http.get<PermissionCatalogCategoryDto[]>(PERMISSIONS_ENDPOINTS.catalog).pipe(
      map(dtos => (dtos ?? []).map(mapPermissionCatalogDto)),
      catchError(err => throwError(() => err))
    );
  }

  getNodeCatalog(): Observable<NodeCatalogEntry[]> {
    return this.http.get<NodeCatalogEntryDto[]>(PERMISSIONS_ENDPOINTS.nodeCatalog).pipe(
      map(dtos => (dtos ?? []).map(mapNodeCatalogEntryDto)),
      catchError(err => throwError(() => err))
    );
  }
}
