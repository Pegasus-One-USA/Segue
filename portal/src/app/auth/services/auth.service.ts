import { Injectable, inject, signal, computed } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { of, tap } from 'rxjs';
import { TokenService } from './token.service';
import { User, UserRole } from '../models/user.model';
import { LoginRequest, ForgotPasswordRequest, ResetPasswordRequest } from '../models/auth-request.model';

const MOCK_USER: User = {
  id:    'dev-001',
  email: 'dev@fhirbridge.local',
  name:  'Dev User',
  role:  'pipeline-editor',
};

const ROLE_REDIRECT: Record<UserRole, string> = {
  'admin':           '/dashboard',
  'pipeline-editor': '/dashboard',
  'analyst':         '/dashboard',
  'viewer':          '/dashboard',
};

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http   = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly tokens = inject(TokenService);

  readonly currentUser     = signal<User | null>(null);
  readonly isLoading       = signal(false);
  readonly error           = signal<string | null>(null);
  readonly isAuthenticated = computed(() => !!this.currentUser());

  login(_req: LoginRequest) {
    // TODO: replace with real HTTP call when backend is ready
    // return this.http.post<{ token: string; user: User }>('/api/auth/login', _req).pipe(...)
    this.tokens.setToken('mock-token');
    this.currentUser.set(MOCK_USER);
    this.roleRedirect(MOCK_USER.role);
    return of(null);
  }

  logout(): void {
    this.tokens.clearToken();
    this.currentUser.set(null);
    this.router.navigate(['/auth/login']);
  }

  forgotPassword(req: ForgotPasswordRequest) {
    return this.http.post('/api/auth/forgot-password', req);
  }

  resetPassword(req: ResetPasswordRequest) {
    return this.http.post('/api/auth/reset-password', req);
  }

  roleRedirect(role: UserRole): void {
    this.router.navigate([ROLE_REDIRECT[role]]);
  }

  initFromToken(): void {
    const token = this.tokens.getToken();
    if (!token) return;
    const payload = this.tokens.decodePayload(token);
    if (!payload) return;
    this.currentUser.set(payload['user'] as User);
  }
}
