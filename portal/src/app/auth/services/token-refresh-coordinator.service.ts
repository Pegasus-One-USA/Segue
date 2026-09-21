import { Injectable } from '@angular/core';
import { Observable, from, firstValueFrom } from 'rxjs';
import { finalize, shareReplay } from 'rxjs/operators';

/**
 * Coalesces concurrent refresh attempts into one in-flight request — WITHIN one tab, and (via the
 * Web Locks API) ACROSS every tab open on this origin.
 *
 * Why this exists: the backend ROTATES the refresh token on every use (each call
 * returns a brand new refresh token, invalidating the old one). If a page fires five
 * parallel API calls right as the access token expires, all five get a 401 around the
 * same time — without this, each would try to refresh with the SAME (still-valid at
 * that instant) refresh token, the first response would rotate it, and the other four
 * would fail with an invalid/already-used refresh token, logging the user out despite
 * a perfectly healthy session. `shareReplay(1)` makes every caller during the window
 * share the one real HTTP call and its single result.
 *
 * The exact same problem happens ACROSS tabs, not just within one: two tabs on the same
 * login, each with their own instance of this service (`providedIn: 'root'` is one
 * instance per Angular app bootstrap, i.e. per tab), can both have their access token
 * expire around the same moment and both attempt to refresh with the same refresh-token
 * cookie at once. Whichever request the server processes second finds that hash already
 * rotated away by the first and gets logged out — showing the "session expired" prompt
 * in a tab the user was actively working in, with nothing actually wrong with their
 * session. `navigator.locks` serializes the real refresh call across every tab on this
 * origin; LAST_REFRESH_KEY then lets a tab that just finished waiting for the lock skip
 * its own now-redundant refresh entirely when another tab already refreshed moments ago
 * — the cookie is already fresh, so the interceptor's retry of the original request
 * succeeds without this tab sending a second, doomed-to-fail refresh call of its own.
 *
 * HIPAA #7: the refresh call itself no longer takes/returns a raw token (both now live
 * in HttpOnly cookies) — genericized to `T` (the rebuilt `User`) so the coordinator no
 * longer needs to know the token shape at all.
 */
@Injectable({ providedIn: 'root' })
export class TokenRefreshCoordinator {
  private inFlight$: Observable<unknown> | null = null;

  private static readonly LOCK_NAME = 'fhirbridge-token-refresh';
  private static readonly LAST_REFRESH_KEY = 'fhirbridge_last_token_refresh_at';
  // Comfortably wider than any realistic cross-tab request-issue skew, but short enough that a tab
  // which genuinely needs its own refresh a few seconds later (e.g. its own token independently
  // expired) is never blocked from getting one.
  private static readonly RECENT_REFRESH_WINDOW_MS = 5_000;
  // Bounds how long a tab waits for another tab's held lock — matches the 10s bound
  // SessionExpiredDialogService.logOutAndRedirect() already applies to its own logout call, and for
  // the same reason: a tab suspended (backgrounded on mobile, a paused debugger) while holding the
  // lock must not leave every other tab's refresh hanging forever with a stuck spinner.
  private static readonly LOCK_WAIT_TIMEOUT_MS = 10_000;

  /**
   * @param startRefresh The real network call (e.g. `authService.refreshToken()`), which also carries
   *   this refresh's side effects (rotating cookies server-side, updating AuthStore from the tap() on
   *   the response).
   * @param onAlreadyRefreshed Invoked instead of `startRefresh` when this tab discovers, after
   *   acquiring the cross-tab lock, that another tab already refreshed moments ago. `startRefresh`
   *   itself must NOT be called in that case — the cookie it would send has already been rotated away
   *   and the call would fail — but this tab's own AuthStore still needs the same side effects
   *   `startRefresh` would have applied (e.g. updated permissions/role claims), or it silently drifts
   *   out of sync with the fresh session every other tab now has. Typically a lighter call than
   *   `startRefresh` (e.g. `GET /auth/me` instead of `/auth/refresh`) that applies the same
   *   store-updating side effect. If omitted, the skip path returns `undefined` and applies no side
   *   effect at all — only safe for a caller that doesn't need one.
   */
  refresh<T>(startRefresh: () => Observable<T>, onAlreadyRefreshed?: () => Observable<T>): Observable<T> {
    if (!this.inFlight$) {
      this.inFlight$ = from(this.refreshWithCrossTabLock(startRefresh, onAlreadyRefreshed)).pipe(
        finalize(() => { this.inFlight$ = null; }),
        shareReplay(1),
      );
    }
    return this.inFlight$ as Observable<T>;
  }

  private async refreshWithCrossTabLock<T>(
    startRefresh: () => Observable<T>,
    onAlreadyRefreshed?: () => Observable<T>,
  ): Promise<T | undefined> {
    const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
    if (!locks) {
      // No Web Locks API (very old browser) — falls back to the single-tab-only coalescing this
      // class always provided; cross-tab races remain possible there, same as before this change.
      return firstValueFrom(startRefresh());
    }

    try {
      return await locks.request(
        TokenRefreshCoordinator.LOCK_NAME,
        { signal: AbortSignal.timeout(TokenRefreshCoordinator.LOCK_WAIT_TIMEOUT_MS) },
        async () => {
          if (Date.now() - TokenRefreshCoordinator.readLastRefreshAt() < TokenRefreshCoordinator.RECENT_REFRESH_WINDOW_MS) {
            // Another tab refreshed while we were waiting for the lock. The cookie is already fresh —
            // the interceptor's retry of the original request will succeed against it — but this tab's
            // own AuthStore still needs whatever side effect startRefresh() would have applied.
            if (onAlreadyRefreshed) {
              return await firstValueFrom(onAlreadyRefreshed());
            }
            return undefined;
          }

          const result = await firstValueFrom(startRefresh());
          TokenRefreshCoordinator.writeLastRefreshAt();
          return result;
        },
      );
    } catch (error) {
      // Signal-abort rejections should surface as the timeout's own TimeoutError per the Web Locks spec,
      // but AbortError is accepted too — belt-and-braces against an implementation that reports the
      // abort itself rather than its reason.
      if (error instanceof DOMException && (error.name === 'TimeoutError' || error.name === 'AbortError')) {
        // The lock never became available within the bound above (most likely another tab is stuck
        // holding it) — refresh unlocked rather than hang this tab's request forever. This reopens the
        // cross-tab race for this one call, same as the no-Locks-API fallback above; better than a
        // permanently stuck spinner over a session that is otherwise fine.
        return firstValueFrom(startRefresh());
      }
      throw error;
    }
  }

  /** Guarded the same way TokenService treats every localStorage access: some browsers/enterprise
   *  policies (Safari's "Block All Cookies", certain embedded webviews) throw SecurityError on
   *  PROPERTY ACCESS, not just on read/write failure. That throw would otherwise reject the lock
   *  promise and, through the interceptor's catchError, force-log-out a user whose session is
   *  perfectly fine — the exact regression this class exists to prevent, just from a new angle. */
  private static readLastRefreshAt(): number {
    try {
      return Number(localStorage.getItem(TokenRefreshCoordinator.LAST_REFRESH_KEY) ?? 0);
    } catch {
      return 0;
    }
  }

  /** Failing silently here just means the skip-window never engages for this tab (every refresh
   *  proceeds as if no other tab had just refreshed) — degraded, not broken: the lock alone still
   *  serializes the calls, so the worst case is one harmless extra refresh, never a spurious logout. */
  private static writeLastRefreshAt(): void {
    try {
      localStorage.setItem(TokenRefreshCoordinator.LAST_REFRESH_KEY, String(Date.now()));
    } catch {
      /* see doc comment above */
    }
  }
}
