import { HttpInterceptorFn, HttpErrorResponse, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { TokenService } from '../services/token.service';
import { AuthService } from '../services/auth.service';
import { TokenRefreshCoordinator } from '../services/token-refresh-coordinator.service';

function withAuthHeader(req: HttpRequest<unknown>, token: string): HttpRequest<unknown> {
  return req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}

/**
 * Attaches the access token to every request, and on a 401, attempts exactly one
 * silent refresh-and-retry before giving up — previously this interceptor logged the
 * user out on every 401 even though a working refresh endpoint (and a working
 * AuthService.refreshToken()) already existed and simply had no caller.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tokens      = inject(TokenService);
  const router      = inject(Router);
  const authService = inject(AuthService);
  const coordinator = inject(TokenRefreshCoordinator);

  const accessToken = tokens.getAccessToken();
  const cloned = accessToken ? withAuthHeader(req, accessToken) : req;

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
      if (err.status !== 401 || isAuthEndpoint) {
        return throwError(() => err);
      }

      const refreshToken = tokens.getRefreshToken();
      if (!refreshToken) {
        return forceLogout(err);
      }

      return coordinator.refresh(() => authService.refreshToken(refreshToken)).pipe(
        switchMap(pair => next(withAuthHeader(req, pair.accessToken))),
        catchError(() => forceLogout(err)),
      );
    })
  );
};
