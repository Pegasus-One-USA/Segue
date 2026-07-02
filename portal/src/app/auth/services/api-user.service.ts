import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { map, catchError } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { IUserService } from './i-user.service';
import { CreateUserRequest, UpdateUserRequest, InviteUserRequest } from '../models/auth-request.model';
import {
  User, UserRole, UserQueryParams, PaginatedResponse, MessageResponse,
  UserManagementDto, RoleDto, PermissionDto, Role, Permission,
} from '../models/user.model';

function mapDto(dto: UserManagementDto): User {
  const parts     = dto.displayName.trim().split(/\s+/);
  const firstName = parts[0] ?? '';
  const lastName  = parts.slice(1).join(' ');
  const roleMap: Record<string, UserRole> = {
    'admin':          'system-admin',
    'unifiedadmin':   'system-admin',
    'tenantadmin':    'tenant-admin',
    'developer':      'developer',
    'pipelineeditor': 'pipeline-editor',
    'reviewer':       'reviewer',
    'auditor':        'auditor',
    'analyst':        'analyst',
    'viewer':         'viewer',
  };
  const rawRole = (dto.globalRoleNames[0] ?? '').toLowerCase().replace(/[\s-]/g, '');
  const role: UserRole = roleMap[rawRole] ?? 'viewer';
  return {
    id:                 dto.id,
    email:              dto.email,
    firstName,
    lastName,
    fullName:           dto.displayName,
    role,
    roles:              [],
    permissions:        [],
    orgId:              '',
    status:             dto.isEnabled ? 'active' : 'inactive',
    loginType:          dto.isLocalLoginEnabled ? 'local' : 'sso',
    mustChangePassword: dto.mustChangePassword,
    emailVerified:      true,
    twoFactorEnabled:   false,
    lastLoginAt:        dto.lastLoginOnUtc ?? undefined,
    createdAt:          dto.createdOnUtc,
    updatedAt:          dto.createdOnUtc,
    _globalRoleNames:   dto.globalRoleNames,
  } as any;
}

const notImpl = () => throwError(() => new Error('Not implemented — backend endpoint not yet available'));

@Injectable({ providedIn: 'root' })
export class ApiUserService extends IUserService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBase}/api/v1`;

  getUsers(params?: UserQueryParams): Observable<PaginatedResponse<User>> {
    return this.http.get<UserManagementDto[]>(`${this.base}/users`).pipe(
      map(dtos => {
        let items = dtos.map(mapDto);

        // search
        if (params?.search) {
          const q = params.search.toLowerCase();
          items = items.filter(u =>
            u.fullName.toLowerCase().includes(q) ||
            u.email.toLowerCase().includes(q)
          );
        }

        // role filter (match against _globalRoleNames)
        if (params?.role) {
          const targetRole = params.role.toLowerCase().replace(/[- ]/g, '');
          items = items.filter(u =>
            ((u as any)._globalRoleNames as string[]).some(
              (r: string) => r.toLowerCase().replace(/[\s-]/g, '') === targetRole
            )
          );
        }

        // status filter
        if (params?.status) {
          items = items.filter(u => u.status === params.status);
        }

        // sort
        const sortKey = params?.sortBy ?? 'createdAt';
        const dir     = params?.sortOrder === 'asc' ? 1 : -1;
        items = [...items].sort((a: any, b: any) => {
          const av = String(a[sortKey] ?? '');
          const bv = String(b[sortKey] ?? '');
          return av < bv ? -dir : av > bv ? dir : 0;
        });

        // paginate
        const page    = params?.page    ?? 1;
        const perPage = params?.perPage ?? 25;
        const total   = items.length;
        return {
          data:       items.slice((page - 1) * perPage, page * perPage),
          total,
          page,
          perPage,
          totalPages: Math.ceil(total / perPage),
        };
      }),
      catchError(err => {
        console.error('ApiUserService.getUsers failed', err);
        return throwError(() => err);
      })
    );
  }

  getUser(id: string): Observable<User> {
    return this.http.get<UserManagementDto>(`${this.base}/users/${id}`).pipe(
      map(mapDto),
      catchError(err => throwError(() => err))
    );
  }

  getUserRoles(userId: string): Observable<RoleDto[]> {
    return this.http.get<RoleDto[]>(`${this.base}/users/${userId}/roles`);
  }

  getPermissions(): Observable<Permission[]> {
    return this.http.get<PermissionDto[]>(`${this.base}/permissions`).pipe(
      map(dtos => dtos.map(d => ({
        id:          d.id,
        name:        d.name,
        resource:    '',
        action:      '',
        description: d.description,
      })))
    );
  }

  getRoles(): Observable<Role[]>                                     { return notImpl() as any; }
  createUser(_req: CreateUserRequest): Observable<User>              { return notImpl() as any; }
  updateUser(_id: string, _req: UpdateUserRequest): Observable<User> { return notImpl() as any; }
  deleteUser(_id: string): Observable<void>                          { return notImpl() as any; }
  enableUser(_id: string): Observable<User>                          { return notImpl() as any; }
  disableUser(_id: string): Observable<User>                         { return notImpl() as any; }
  suspendUser(_id: string): Observable<User>                         { return notImpl() as any; }
  assignRole(_userId: string, _roleId: string): Observable<User>     { return notImpl() as any; }
  removeRole(_userId: string, _roleId: string): Observable<User>     { return notImpl() as any; }
  inviteUser(_req: InviteUserRequest): Observable<MessageResponse>   { return notImpl() as any; }
  resendInvitation(_userId: string): Observable<MessageResponse>     { return notImpl() as any; }
  resetUserPassword(_userId: string): Observable<MessageResponse>    { return notImpl() as any; }
}
