import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { map, catchError, switchMap } from 'rxjs/operators';
import { USERS_ENDPOINTS, ROLES_ENDPOINTS, PERMISSIONS_ENDPOINTS } from '../../core/api-endpoints';
import { IUserService } from './i-user.service';
import { CreateUserRequest, UpdateUserRequest, InviteUserRequest } from '../models/auth-request.model';
import {
  User, UserRole, UserStatus, UserQueryParams, PaginatedResponse, MessageResponse,
  UserManagementDto, UserDetailDto, RoleDto, PermissionDto, Role, Permission,
  BackendUserStatus, InviteResult,
} from '../models/user.model';

// ─── Role-name → front-end UserRole mapping ──────────────────────────────────
const ROLE_MAP: Record<string, UserRole> = {
  'admin':          'system-admin',
  'superadmin':     'system-admin',
  'globaladmin':    'system-admin',
  'unifiedadmin':   'system-admin',
  'systemadmin':    'system-admin',
  'tenantadmin':    'tenant-admin',
  'developer':      'developer',
  'pipelineeditor': 'pipeline-editor',
  'reviewer':       'reviewer',
  'auditor':        'auditor',
  'analyst':        'analyst',
  'viewer':         'viewer',
  // Added for the real backend's seeded roles (UnifiedRoles.cs), which don't match
  // the frontend's original 8-role taxonomy — 'audit'/'operations' had no entry before.
  'audit':          'auditor',
  'operations':     'pipeline-editor',
};

// Exported (was private) so auth-api.service.ts maps backend claim-role names the same way.
export function toUserRole(name: string | undefined): UserRole {
  const raw = (name ?? '').toLowerCase().replace(/[\s-]/g, '');
  return ROLE_MAP[raw] ?? 'viewer';
}

/** Backend numeric status (1 Invited / 2 Active / 3 Inactive) → front-end string. */
function toStatus(status: BackendUserStatus | undefined, isEnabled: boolean): UserStatus {
  switch (status) {
    case 1:  return 'pending';   // Invited
    case 2:  return 'active';
    case 3:  return 'inactive';
    default: return isEnabled ? 'active' : 'inactive';
  }
}

// Exported so auth-api.service.ts's login mapping shares this instead of a second literal.
export const DEFAULT_ROLE_COLOR = '#64748B';

// Exported so auth-api.service.ts can reuse this instead of re-implementing name-splitting.
export function splitName(displayName: string, first?: string, last?: string): { firstName: string; lastName: string } {
  if (first || last) return { firstName: first ?? '', lastName: last ?? '' };
  const parts = (displayName ?? '').trim().split(/\s+/);
  return { firstName: parts[0] ?? '', lastName: parts.slice(1).join(' ') };
}

// Exported (was private) so api-role.service.ts can map RoleDto/PermissionDto identically.
export function mapRoleDto(dto: RoleDto): Role {
  return {
    id:           dto.id,
    name:         toUserRole(dto.name),
    displayName:  dto.name,
    description:  dto.description,
    permissions:  (dto.permissions ?? []).map(mapPermissionDto),
    color:        DEFAULT_ROLE_COLOR,
    isSystemRole: dto.isSystemRole,
    createdAt:    '',
  };
}

export function mapPermissionDto(dto: PermissionDto): Permission {
  const [resource, action] = (dto.name ?? '').split(/[.:]/);
  return {
    id:          dto.id,
    name:        dto.name,
    resource:    resource ?? '',
    action:      action ?? '',
    description: dto.description,
  };
}

function mapListDto(dto: UserManagementDto): User {
  const { firstName, lastName } = splitName(dto.displayName);
  return {
    id:                 dto.id,
    email:              dto.email,
    firstName,
    lastName,
    fullName:           dto.displayName,
    role:               toUserRole(dto.globalRoleNames?.[0]),
    roles:              [],
    permissions:        [],
    orgId:              '',
    status:             toStatus(dto.status, dto.isEnabled),
    loginType:          dto.isLocalLoginEnabled ? 'local' : 'sso',
    mustChangePassword: dto.mustChangePassword,
    emailVerified:      true,
    twoFactorEnabled:   false,
    lastLoginAt:        dto.lastLoginOnUtc ?? undefined,
    createdAt:          dto.createdOnUtc,
    updatedAt:          dto.createdOnUtc,
    _globalRoleNames:   dto.globalRoleNames ?? [],
  } as unknown as User;
}

function mapDetailDto(dto: UserDetailDto): User {
  const { firstName, lastName } = splitName(dto.displayName, dto.firstName, dto.lastName);
  const roles = (dto.roles ?? []).map(mapRoleDto);
  return {
    id:                 dto.id,
    email:              dto.email,
    firstName,
    lastName,
    fullName:           dto.displayName || `${firstName} ${lastName}`.trim(),
    role:               roles[0]?.name ?? 'viewer',
    roles,
    permissions:        [...new Map(roles.flatMap(r => r.permissions).map(p => [p.id, p])).values()],
    orgId:              '',
    status:             toStatus(dto.status, dto.isEnabled),
    loginType:          'local',
    mustChangePassword: false,
    emailVerified:      true,
    twoFactorEnabled:   false,
    lastLoginAt:        dto.lastLoginOnUtc ?? undefined,
    createdAt:          dto.createdOnUtc,
    updatedAt:          dto.createdOnUtc,
    _globalRoleNames:   (dto.roles ?? []).map(r => r.name),
  } as unknown as User;
}

function toInviteResult(dto: UserDetailDto, fallbackEmail: string): InviteResult {
  const token = dto.invitationToken;
  const email = dto.email || fallbackEmail;
  return {
    success:         true,
    message:         `Invitation ready for ${email}.`,
    email,
    invitationToken: token,
    invitationLink:  token
      ? `${location.origin}/auth/set-password?token=${token}&email=${encodeURIComponent(email)}`
      : undefined,
  };
}

const notImpl = () =>
  throwError(() => new Error('Not implemented — backend endpoint not yet available'));

@Injectable({ providedIn: 'root' })
export class ApiUserService extends IUserService {
  private readonly http = inject(HttpClient);

  // ─── List ──────────────────────────────────────────────────────────────────
  getUsers(params?: UserQueryParams): Observable<PaginatedResponse<User>> {
    return this.http.get<UserManagementDto[]>(USERS_ENDPOINTS.list).pipe(
      map(dtos => {
        let items = (dtos ?? []).map(mapListDto);

        if (params?.search) {
          const q = params.search.toLowerCase();
          items = items.filter(u =>
            u.fullName.toLowerCase().includes(q) ||
            u.email.toLowerCase().includes(q)
          );
        }

        if (params?.role) {
          const target = params.role.toLowerCase().replace(/[\s-]/g, '');
          items = items.filter(u =>
            ((u as any)._globalRoleNames as string[]).some(
              r => r.toLowerCase().replace(/[\s-]/g, '') === target
            )
          );
        }

        if (params?.status) {
          items = items.filter(u => u.status === params.status);
        }

        const sortKey = params?.sortBy ?? 'createdAt';
        const dir     = params?.sortOrder === 'asc' ? 1 : -1;
        items = [...items].sort((a: any, b: any) => {
          const av = String(a[sortKey] ?? '');
          const bv = String(b[sortKey] ?? '');
          return av < bv ? -dir : av > bv ? dir : 0;
        });

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

  // ─── Get one ───────────────────────────────────────────────────────────────
  getUser(id: string): Observable<User> {
    return this.http.get<UserDetailDto>(USERS_ENDPOINTS.byId(id)).pipe(
      map(mapDetailDto),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Invite ────────────────────────────────────────────────────────────────
  inviteUser(req: InviteUserRequest): Observable<InviteResult> {
    const body = {
      email:     req.email,
      roleId:    req.roleId,
      firstName: req.firstName || undefined,
      lastName:  req.lastName || undefined,
    };
    return this.http.post<UserDetailDto>(USERS_ENDPOINTS.invite, body).pipe(
      map(dto => toInviteResult(dto, req.email)),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Resend invite ───────────────────────────────────────────────────────
  resendInvitation(userId: string): Observable<InviteResult> {
    return this.http.post<UserDetailDto>(USERS_ENDPOINTS.resendInvite(userId), {}).pipe(
      map(dto => toInviteResult(dto, dto?.email ?? '')),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Delete ────────────────────────────────────────────────────────────────
  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(USERS_ENDPOINTS.byId(id)).pipe(
      catchError(err => throwError(() => err))
    );
  }

  // ─── Activate / Deactivate (status endpoint) ────────────────────────────────
  enableUser(id: string): Observable<User> {
    return this.setStatus(id, true);
  }

  disableUser(id: string): Observable<User> {
    return this.setStatus(id, false);
  }

  private setStatus(id: string, isEnabled: boolean): Observable<User> {
    return this.http.patch<UserDetailDto>(USERS_ENDPOINTS.status(id), { isEnabled }).pipe(
      map(mapDetailDto),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Update ────────────────────────────────────────────────────────────────
  updateUser(id: string, req: UpdateUserRequest): Observable<User> {
    const displayName = [req.firstName, req.lastName].filter(Boolean).join(' ').trim() || undefined;
    const roleNames   = req.role ? [req.role] : [];
    const body = {
      displayName,
      isEnabled:            req.status ? req.status === 'active' : true,
      roleNames,
      requirePasswordChange: false,
    };
    return this.http.put<UserManagementDto>(USERS_ENDPOINTS.byId(id), body).pipe(
      map(mapListDto),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Assign / remove role ────────────────────────────────────────────────
  assignRole(userId: string, roleId: string): Observable<User> {
    return this.http.post<UserDetailDto>(USERS_ENDPOINTS.roles(userId), { roleId }).pipe(
      map(mapDetailDto),
      catchError(err => throwError(() => err))
    );
  }

  removeRole(userId: string, roleId: string): Observable<User> {
    // Backend returns 204; re-fetch the user so callers get the updated role set.
    return this.http.delete<void>(USERS_ENDPOINTS.removeRole(userId, roleId)).pipe(
      switchMap(() => this.getUser(userId)),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Roles (read-only, for dropdowns) ────────────────────────────────────
  getRoles(): Observable<Role[]> {
    return this.http.get<RoleDto[]>(ROLES_ENDPOINTS.list).pipe(
      map(dtos => (dtos ?? []).map(mapRoleDto)),
      catchError(err => throwError(() => err))
    );
  }

  getPermissions(): Observable<Permission[]> {
    return this.http.get<PermissionDto[]>(PERMISSIONS_ENDPOINTS.list).pipe(
      map(dtos => (dtos ?? []).map(mapPermissionDto)),
      catchError(err => throwError(() => err))
    );
  }

  // ─── Not backed by an endpoint yet ───────────────────────────────────────
  createUser(_req: CreateUserRequest): Observable<User>          { return notImpl() as any; }
  suspendUser(_id: string): Observable<User>                     { return notImpl() as any; }
  resetUserPassword(_userId: string): Observable<MessageResponse>{ return notImpl() as any; }
}
