/**
 * AuthApiService — Real HTTP implementation.
 * Replace MockAuthService with this class when the backend is ready:
 *   app.config.ts:  { provide: IAuthService, useClass: AuthApiService }
 */
import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { IAuthService } from './i-auth.service';
import { User, MessageResponse, TokenPair } from '../models/user.model';
import {
  LoginRequest, LoginResponse,
  RegisterRequest, RegisterResponse,
  ForgotPasswordRequest, ResetPasswordRequest, ChangePasswordRequest,
} from '../models/auth-request.model';

const BASE = '/api/auth';

@Injectable({ providedIn: 'root' })
export class AuthApiService extends IAuthService {
  private readonly http = inject(HttpClient);

  override login(req: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${BASE}/login`, req);
  }

  override logout(): Observable<void> {
    return this.http.post<void>(`${BASE}/logout`, {});
  }

  override register(req: RegisterRequest): Observable<RegisterResponse> {
    return this.http.post<RegisterResponse>(`${BASE}/register`, req);
  }

  override forgotPassword(req: ForgotPasswordRequest): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/forgot-password`, req);
  }

  override resetPassword(req: ResetPasswordRequest): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/reset-password`, req);
  }

  override changePassword(req: ChangePasswordRequest): Observable<MessageResponse> {
    return this.http.post<MessageResponse>(`${BASE}/change-password`, req);
  }

  override getCurrentUser(): Observable<User> {
    return this.http.get<User>(`${BASE}/me`);
  }

  override refreshToken(refreshToken: string): Observable<TokenPair> {
    return this.http.post<TokenPair>(`${BASE}/refresh`, { refreshToken });
  }
}
