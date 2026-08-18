import { HttpInterceptorFn, HttpErrorResponse, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { TokenService } from '../services/token.service';
import { AuthService } from '../services/auth.service';
import { TokenRefreshCoordinator } from '../services/token-refresh-coordinator.service';

const STATE_CHANGING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/**
 * HIPAA #20/#7: refuses to send the request WITH credentials to a non-HTTPS absolute URL — a
 * misconfigured `environment.apiBaseUrl` should never cause the session cookie to be sent in the
 * clear. Relative URLs (same-origin) and localhost (dev) are allowed.
 */
function isSafeForCredentials(url: string): boolean {
  if (!/^https?:\/\//i.test(url)) {
    return true; // relative URL — same origin as the page, not a separate scheme decision.
  }

  if (/^https:\/\//i.test(url)) {
    return true;
  }

  return /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?\//i.test(url);
}

/**
 * HIPAA #7: the access token lives in an HttpOnly cookie the browser attaches automatically —
 * this interceptor no longer reads or attaches an Authorization header at all. What it still does:
 *  1. Sends every request `withCredentials: true` so the cookie (and CSRF cookie) actually go out,
 *     including cross-origin in local dev (portal :4200, API :5000).
 *  2. Echoes the double-submit CSRF cookie back as a header on state-changing requests — the
 *     backend rejects those without it (see AuthController's CSRF middleware in Program.cs).
 *  3. On a 401, attempts exactly one silent refresh-and-retry (the refresh call itself relies on
 *     the refresh-token cookie) before giving up.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tokens      = inject(TokenService);
  const router      = inject(Router);
  const authService = inject(AuthService);
  const coordinator = inject(TokenRefreshCoordinator);

  const withCreds = isSafeForCredentials(req.url);
  if (!withCreds) {
    console.warn(`Refusing to send credentials to non-HTTPS request: ${req.url}`);
  }

  const headers: Record<string, string> = {};
  if (withCreds && STATE_CHANGING_METHODS.has(req.method.toUpperCase())) {
    const csrfToken = tokens.getCsrfToken();
    if (csrfToken) {
      headers['X-CSRF-Token'] = csrfToken;
    }
  }

  const cloned = req.clone({ withCredentials: withCreds, setHeaders: headers });

  const forceLogout = (err: HttpErrorResponse): Observable<never> => {
    tokens.clearTokens();
    router.navigate(['/auth/login']);
    return throwError(() => err);
  };

  return next(cloned).pipe(
    catchError((err: HttpErrorResponse) => {
      // Never try to refresh-and-retry a failed login or a failed refresh call itself —
      // that would either mask a real "wrong password" error or infinite-loop on a
      // refresh endpoint that's itself returning 401.
      const isAuthEndpoint = req.url.includes('/auth/');
      if (err.status !== 401 || isAuthEndpoint || !withCreds) {
        return throwError(() => err);
      }

      return coordinator.refresh(() => authService.refreshToken()).pipe(
        switchMap(() => next(cloned)),
        catchError(() => forceLogout(err)),
      );
    })
  );
};
