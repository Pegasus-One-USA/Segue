import { Injectable } from '@angular/core';

// HIPAA #7: the access/refresh tokens live in HttpOnly cookies the backend sets directly — this
// service no longer reads or stores the raw JWT anywhere JS can reach it. What's left:
//  - the "remember me" preference (never sensitive on its own)
//  - a non-sensitive session marker, written purely so logging in/out in one tab fires a native
//    `storage` event that CrossTabAuthSyncService listens for in every other open tab
//  - a CSRF token reader, since the double-submit cookie IS deliberately non-HttpOnly/JS-readable
const REMEMBER_KEY = 'fb_remember';
// Exported so CrossTabAuthSyncService can identify which localStorage key changed in a
// `storage` event without duplicating the literal string or reaching into private state.
export const SESSION_MARKER_KEY = 'fb_session_marker';
const CSRF_COOKIE_NAME = 'fhirbridge_csrf';

@Injectable({ providedIn: 'root' })
export class TokenService {
  get isRemembered(): boolean {
    return localStorage.getItem(REMEMBER_KEY) === 'true';
  }

  setRememberMe(value: boolean): void {
    localStorage.setItem(REMEMBER_KEY, String(value));
  }

  /** Call once a session is established (login/refresh) — fires a cross-tab `storage` event. */
  markSessionActive(): void {
    localStorage.setItem(SESSION_MARKER_KEY, Date.now().toString());
  }

  /** True if a cookie session appears active — a best-effort UI hint (e.g. for route guards), not
   *  an authentication decision: the server is always the actual authority via the HttpOnly cookie. */
  hasSession(): boolean {
    return this.getCsrfToken() !== null;
  }

  /** Reads the non-HttpOnly double-submit CSRF cookie so the interceptor can echo it back as a header. */
  getCsrfToken(): string | null {
    const match = document.cookie.match(new RegExp(`(?:^|; )${CSRF_COOKIE_NAME}=([^;]*)`));
    return match ? decodeURIComponent(match[1]) : null;
  }

  /** Clears local session state. Does NOT clear the HttpOnly cookies themselves — only the server's
   *  Set-Cookie response (see AuthController.Logout) can do that. */
  clearTokens(): void {
    localStorage.removeItem(SESSION_MARKER_KEY);
    localStorage.removeItem(REMEMBER_KEY);
  }

  // ─── Legacy shims (keep existing callers working) ──────────────────────────
  clearToken(): void { this.clearTokens(); }
}
