import { Injectable } from '@angular/core';
import { Observable, of, throwError } from 'rxjs';
import { delay, switchMap } from 'rxjs/operators';
import { IUserService } from './i-user.service';
import {
  User, Role, Permission,
  PaginatedResponse, MessageResponse, UserQueryParams, UserStatus,
} from '../models/user.model';
import { CreateUserRequest, UpdateUserRequest, InviteUserRequest } from '../models/auth-request.model';
import { MOCK_USERS, ALL_ROLES, ALL_PERMISSIONS, DEFAULT_PASSWORD } from '../mock/mock-db';

@Injectable({ providedIn: 'root' })
export class MockUserService extends IUserService {

  // ─── Get Users (with filter, sort, pagination) ────────────────────────────
  override getUsers(params: UserQueryParams = {}): Observable<PaginatedResponse<User>> {
    return of(null).pipe(
      delay(500),
      switchMap(() => {
        const page    = params.page    ?? 1;
        const perPage = params.perPage ?? 10;

        let filtered = MOCK_USERS.filter(u => {
          const q = params.search?.toLowerCase() ?? '';
          if (q && !u.fullName.toLowerCase().includes(q) && !u.email.toLowerCase().includes(q)) return false;
          if (params.role   && u.role   !== params.role)   return false;
          if (params.status && u.status !== params.status) return false;
          return true;
        });

        // Sort
        if (params.sortBy) {
          const dir = params.sortOrder === 'desc' ? -1 : 1;
          filtered = filtered.sort((a, b) => {
            const av = (a as unknown as Record<string, unknown>)[params.sortBy!] as string ?? '';
            const bv = (b as unknown as Record<string, unknown>)[params.sortBy!] as string ?? '';
            return av.localeCompare(bv) * dir;
          });
        }

        const total      = filtered.length;
        const totalPages = Math.ceil(total / perPage);
        const data       = filtered.slice((page - 1) * perPage, page * perPage)
                                   .map(u => this.sanitise(u));

        return of<PaginatedResponse<User>>({ data, total, page, perPage, totalPages });
      })
    );
  }

  // ─── Get Single User ──────────────────────────────────────────────────────
  override getUser(id: string): Observable<User> {
    return of(null).pipe(
      delay(300),
      switchMap(() => {
        const u = MOCK_USERS.find(u => u.id === id);
        if (!u) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        return of(this.sanitise(u));
      })
    );
  }

  // ─── Create User ──────────────────────────────────────────────────────────
  override createUser(req: CreateUserRequest): Observable<User> {
    return of(null).pipe(
      delay(800),
      switchMap(() => {
        const exists = MOCK_USERS.find(u => u.email.toLowerCase() === req.email.toLowerCase());
        if (exists) return throwError(() => ({ code: 'EMAIL_TAKEN', message: 'A user with this email already exists.' }));

        const roleObj = ALL_ROLES.find(r => r.name === req.role)!;
        const newUser: User = {
          id:                 `u-${Date.now()}`,
          email:              req.email.trim().toLowerCase(),
          firstName:          req.firstName.trim(),
          lastName:           req.lastName.trim(),
          fullName:           `${req.firstName.trim()} ${req.lastName.trim()}`,
          role:               req.role,
          roles:              [roleObj],
          permissions:        roleObj.permissions,
          orgId:              'org-001',
          department:         req.department,
          jobTitle:           req.jobTitle,
          status:             req.status,
          loginType:          req.loginType,
          mustChangePassword: req.sendInvite ?? false,
          emailVerified:      !req.sendInvite,
          twoFactorEnabled:   false,
          createdAt:          new Date().toISOString(),
          updatedAt:          new Date().toISOString(),
          passwordHash:       DEFAULT_PASSWORD,
        };

        MOCK_USERS.push(newUser);
        return of(this.sanitise(newUser));
      })
    );
  }

  // ─── Update User ──────────────────────────────────────────────────────────
  override updateUser(id: string, req: UpdateUserRequest): Observable<User> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        const idx = MOCK_USERS.findIndex(u => u.id === id);
        if (idx === -1) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));

        const existing = MOCK_USERS[idx];
        const roleObj  = req.role ? ALL_ROLES.find(r => r.name === req.role)! : null;

        const updated: User = {
          ...existing,
          ...req,
          ...(roleObj ? { roles: [roleObj], permissions: roleObj.permissions } : {}),
          fullName:  `${req.firstName ?? existing.firstName} ${req.lastName ?? existing.lastName}`,
          updatedAt: new Date().toISOString(),
        };

        MOCK_USERS[idx] = updated;
        return of(this.sanitise(updated));
      })
    );
  }

  // ─── Delete User ──────────────────────────────────────────────────────────
  override deleteUser(id: string): Observable<void> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        const idx = MOCK_USERS.findIndex(u => u.id === id);
        if (idx === -1) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        MOCK_USERS.splice(idx, 1);
        return of<void>(undefined);
      })
    );
  }

  // ─── Enable / Disable / Suspend ───────────────────────────────────────────
  override enableUser(id: string): Observable<User> {
    return this.setStatus(id, 'active');
  }

  override disableUser(id: string): Observable<User> {
    return this.setStatus(id, 'inactive');
  }

  override suspendUser(id: string): Observable<User> {
    return this.setStatus(id, 'suspended');
  }

  private setStatus(id: string, status: UserStatus): Observable<User> {
    return of(null).pipe(
      delay(400),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.id === id);
        if (!user) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        user.status    = status;
        user.updatedAt = new Date().toISOString();
        return of(this.sanitise(user));
      })
    );
  }

  // ─── Assign / Remove Role ─────────────────────────────────────────────────
  override assignRole(userId: string, roleId: string): Observable<User> {
    return of(null).pipe(
      delay(400),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.id === userId);
        const role = ALL_ROLES.find(r => r.id === roleId);
        if (!user) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        if (!role) return throwError(() => ({ code: 'NOT_FOUND', message: 'Role not found.' }));

        const alreadyHas = user.roles.some(r => r.id === roleId);
        if (!alreadyHas) {
          user.roles.push(role);
          user.role        = role.name;
          user.permissions = [...new Map(
            [...user.permissions, ...role.permissions].map(p => [p.id, p])
          ).values()];
          user.updatedAt   = new Date().toISOString();
        }
        return of(this.sanitise(user));
      })
    );
  }

  override removeRole(userId: string, roleId: string): Observable<User> {
    return of(null).pipe(
      delay(400),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.id === userId);
        if (!user) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        user.roles       = user.roles.filter(r => r.id !== roleId);
        user.role        = user.roles[0]?.name ?? 'viewer';
        user.updatedAt   = new Date().toISOString();
        return of(this.sanitise(user));
      })
    );
  }

  // ─── Roles & Permissions ──────────────────────────────────────────────────
  override getRoles(): Observable<Role[]> {
    return of(ALL_ROLES).pipe(delay(300));
  }

  override getPermissions(): Observable<Permission[]> {
    return of(ALL_PERMISSIONS).pipe(delay(300));
  }

  // ─── Invite User ──────────────────────────────────────────────────────────
  override inviteUser(req: InviteUserRequest): Observable<MessageResponse> {
    return of(null).pipe(
      delay(900),
      switchMap(() => {
        const exists = MOCK_USERS.find(u => u.email.toLowerCase() === req.email.toLowerCase());
        if (exists) return throwError(() => ({ code: 'EMAIL_TAKEN', message: 'A user with this email already exists.' }));

        const roleObj = ALL_ROLES.find(r => r.name === req.role)!;
        const invited: User = {
          id:                 `u-${Date.now()}`,
          email:              req.email.trim().toLowerCase(),
          firstName:          req.firstName.trim(),
          lastName:           req.lastName.trim(),
          fullName:           `${req.firstName.trim()} ${req.lastName.trim()}`,
          role:               req.role,
          roles:              [roleObj],
          permissions:        roleObj.permissions,
          orgId:              'org-001',
          department:         req.department,
          status:             'pending',
          loginType:          'local',
          mustChangePassword: true,
          emailVerified:      false,
          twoFactorEnabled:   false,
          createdAt:          new Date().toISOString(),
          updatedAt:          new Date().toISOString(),
          passwordHash:       DEFAULT_PASSWORD,
        };

        MOCK_USERS.push(invited);
        return of<MessageResponse>({ success: true, message: `Invitation sent to ${req.email}.` });
      })
    );
  }

  // ─── Resend Invitation ────────────────────────────────────────────────────
  override resendInvitation(userId: string): Observable<MessageResponse> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.id === userId);
        if (!user) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        return of<MessageResponse>({ success: true, message: `Invitation resent to ${user.email}.` });
      })
    );
  }

  // ─── Reset User Password ──────────────────────────────────────────────────
  override resetUserPassword(userId: string): Observable<MessageResponse> {
    return of(null).pipe(
      delay(600),
      switchMap(() => {
        const user = MOCK_USERS.find(u => u.id === userId);
        if (!user) return throwError(() => ({ code: 'NOT_FOUND', message: 'User not found.' }));
        user.mustChangePassword = true;
        user.passwordHash       = DEFAULT_PASSWORD;
        return of<MessageResponse>({ success: true, message: `Password reset email sent to ${user.email}.` });
      })
    );
  }

  // ─── Sanitise (remove password hash) ─────────────────────────────────────
  private sanitise(u: User): User {
    const { passwordHash: _, ...safe } = u;
    return safe as User;
  }
}
