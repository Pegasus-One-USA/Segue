/**
 * UserApiService — Real HTTP implementation.
 * Replace MockUserService in app.config.ts when the backend is ready:
 *   { provide: IUserService, useClass: UserApiService }
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { IUserService } from './i-user.service';
import {
  User, Role, Permission, PermissionAllocationDto,
  PaginatedResponse, MessageResponse, UserQueryParams,
} from '../models/user.model';
import { CreateUserRequest, UpdateUserRequest, InviteUserRequest } from '../models/auth-request.model';

const BASE = '/api/users';

@Injectable({ providedIn: 'root' })
export class UserApiService extends IUserService {
  private readonly http = inject(HttpClient);

  private toParams(q: UserQueryParams): HttpParams {
    let p = new HttpParams();
    if (q.page)      p = p.set('page', q.page);
    if (q.perPage)   p = p.set('perPage', q.perPage);
    if (q.search)    p = p.set('search', q.search);
    if (q.role)      p = p.set('role', q.role);
    if (q.status)    p = p.set('status', q.status);
    if (q.sortBy)    p = p.set('sortBy', q.sortBy);
    if (q.sortOrder) p = p.set('sortOrder', q.sortOrder);
    return p;
  }

  override getUsers(params: UserQueryParams = {}): Observable<PaginatedResponse<User>> {
    return this.http.get<PaginatedResponse<User>>(BASE, { params: this.toParams(params) });
  }

  override getUser(id: string): Observable<User> {
    return this.http.get<User>(`${BASE}/${id}`);
  }

  override createUser(req: CreateUserRequest): Observable<User> {
    return this.http.post<User>(BASE, req);
  }

  override updateUser(id: string, req: UpdateUserRequest): Observable<User> {
    return this.http.patch<User>(`${BASE}/${id}`, req);
  }

  override deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/${id}`);
  }

  override enableUser(id: string): Observable<User> {
    return this.http.post<User>(`${BASE}/${id}/enable`, {});
  }

  override disableUser(id: string): Observable<User> {
    return this.http.post<User>(`${BASE}/${id}/disable`, {});
  }

  override suspendUser(id: string): Observable<User> {
    return this.http.post<User>(`${BASE}/${id}/suspend`, {});
  }

  override assignRole(userId: string, roleId: string): Observable<User> {
    return this.http.post<User>(`${BASE}/${userId}/roles`, { roleId });
  }

  override removeRole(userId: string, roleId: string): Observable<User> {
    return this.http.delete<User>(`${BASE}/${userId}/roles/${roleId}`);
  }

  override getRoles(): Observable<Role[]> {
    return this.http.get<Role[]>('/api/roles');
  }

  override getPermissions(): Observable<Permission[]> {
    return this.http.get<Permission[]>('/api/permissions');
  }

  override inviteUser(req: InviteUserRequest): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/invite`, req);
  }

  override resendInvitation(userId: string): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/${userId}/resend-invite`, {});
  }

  override resetUserPassword(userId: string): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/${userId}/reset-password`, {});
  }

  override getUserPermissionAllocations(userId: string): Observable<PermissionAllocationDto[]> {
    return this.http.get<PermissionAllocationDto[]>(`${BASE}/${userId}/permission-allocations`);
  }

  override setUserPermissionAllocation(userId: string, permissionId: string, isEnabled: boolean): Observable<User> {
    return this.http.put<User>(`${BASE}/${userId}/permission-allocations/${permissionId}`, { isEnabled });
  }

  override removeUserPermissionAllocation(userId: string, permissionId: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/${userId}/permission-allocations/${permissionId}`);
  }
}
