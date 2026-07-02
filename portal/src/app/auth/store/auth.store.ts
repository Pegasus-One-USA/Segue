import { Injectable, computed, signal } from '@angular/core';
import { User, UserRole, Permission, Role } from '../models/user.model';
import { Session } from '../models/auth-state.model';

@Injectable({ providedIn: 'root' })
export class AuthStore {

  // ─── State signals ─────────────────────────────────────────────────────────
  readonly currentUser  = signal<User | null>(null);
  readonly isLoading    = signal(false);
  readonly error        = signal<string | null>(null);
  readonly session      = signal<Session | null>(null);

  // ─── Derived computed values ──────────────────────────────────────────────
  readonly isAuthenticated = computed(() => !!this.currentUser());
  readonly roles           = computed((): Role[]       => this.currentUser()?.roles ?? []);
  readonly permissions     = computed((): Permission[] => this.currentUser()?.permissions ?? []);
  readonly userRole        = computed((): UserRole | null => this.currentUser()?.role ?? null);
  readonly initials        = computed((): string => {
    const u = this.currentUser();
    if (!u) return '';
    return (u.firstName[0] + u.lastName[0]).toUpperCase();
  });
  readonly displayName = computed(() => this.currentUser()?.fullName ?? '');

  // ─── Mutations ─────────────────────────────────────────────────────────────
  setUser(user: User | null): void    { this.currentUser.set(user); }
  setLoading(v: boolean): void        { this.isLoading.set(v); }
  setError(msg: string | null): void  { this.error.set(msg); }
  setSession(s: Session | null): void { this.session.set(s); }

  clear(): void {
    this.currentUser.set(null);
    this.session.set(null);
    this.error.set(null);
    this.isLoading.set(false);
  }

  // ─── Permission helpers ────────────────────────────────────────────────────
  hasRole(...roles: UserRole[]): boolean {
    const u = this.currentUser();
    return u ? roles.includes(u.role) : false;
  }

  hasPermission(permName: string): boolean {
    return this.permissions().some(p => p.name === permName);
  }

  hasAnyPermission(...names: string[]): boolean {
    const perms = this.permissions().map(p => p.name);
    return names.some(n => perms.includes(n));
  }

  isAdmin(): boolean {
    return this.hasRole('system-admin', 'tenant-admin');
  }
}
