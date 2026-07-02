import { Injectable } from '@angular/core';
import { JwtPayload } from '../models/auth-state.model';

const ACCESS_KEY  = 'fb_access';
const REFRESH_KEY = 'fb_refresh';
const REMEMBER_KEY = 'fb_remember';

@Injectable({ providedIn: 'root' })
export class TokenService {

  private get storage(): Storage {
    return this.isRemembered ? localStorage : sessionStorage;
  }

  get isRemembered(): boolean {
    return localStorage.getItem(REMEMBER_KEY) === 'true';
  }

  setRememberMe(value: boolean): void {
    localStorage.setItem(REMEMBER_KEY, String(value));
  }

  // ─── Access token ──────────────────────────────────────────────────────────
  getAccessToken(): string | null { return this.storage.getItem(ACCESS_KEY); }

  // ─── Refresh token ─────────────────────────────────────────────────────────
  getRefreshToken(): string | null { return this.storage.getItem(REFRESH_KEY); }

  // ─── Set both ──────────────────────────────────────────────────────────────
  setTokens(accessToken: string, refreshToken: string, rememberMe = false): void {
    this.setRememberMe(rememberMe);
    this.storage.setItem(ACCESS_KEY, accessToken);
    this.storage.setItem(REFRESH_KEY, refreshToken);
  }

  // ─── Clear all ─────────────────────────────────────────────────────────────
  clearTokens(): void {
    [localStorage, sessionStorage].forEach(s => {
      s.removeItem(ACCESS_KEY);
      s.removeItem(REFRESH_KEY);
    });
    localStorage.removeItem(REMEMBER_KEY);
  }

  // ─── Decode JWT payload ────────────────────────────────────────────────────
  decodePayload<T = JwtPayload>(token: string): T | null {
    try {
      const part = token.split('.')[1];
      return JSON.parse(atob(part)) as T;
    } catch {
      return null;
    }
  }

  isExpired(token: string): boolean {
    const p = this.decodePayload<{ exp: number }>(token);
    if (!p?.exp) return true;
    return Date.now() >= p.exp * 1000;
  }

  // ─── Legacy shims (keep existing callers working) ──────────────────────────
  getToken(): string | null  { return this.getAccessToken(); }
  setToken(t: string): void  { this.storage.setItem(ACCESS_KEY, t); }
  clearToken(): void         { this.clearTokens(); }
}
