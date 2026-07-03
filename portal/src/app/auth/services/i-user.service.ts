import { Observable } from 'rxjs';
import {
  User, Role, Permission,
  PaginatedResponse, MessageResponse, UserQueryParams, InviteResult,
} from '../models/user.model';
import {
  CreateUserRequest, UpdateUserRequest, InviteUserRequest,
} from '../models/auth-request.model';

export abstract class IUserService {
  abstract getUsers(params?: UserQueryParams): Observable<PaginatedResponse<User>>;
  abstract getUser(id: string): Observable<User>;
  abstract createUser(req: CreateUserRequest): Observable<User>;
  abstract updateUser(id: string, req: UpdateUserRequest): Observable<User>;
  abstract deleteUser(id: string): Observable<void>;
  abstract enableUser(id: string): Observable<User>;
  abstract disableUser(id: string): Observable<User>;
  abstract suspendUser(id: string): Observable<User>;
  abstract assignRole(userId: string, roleId: string): Observable<User>;
  abstract removeRole(userId: string, roleId: string): Observable<User>;
  abstract getRoles(): Observable<Role[]>;
  abstract getPermissions(): Observable<Permission[]>;
  abstract inviteUser(req: InviteUserRequest): Observable<InviteResult>;
  abstract resendInvitation(userId: string): Observable<InviteResult>;
  abstract resetUserPassword(userId: string): Observable<MessageResponse>;
}
