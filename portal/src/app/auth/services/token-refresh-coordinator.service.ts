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

  refresh<T>(startRefresh: () => Observable<T>): Observable<T> {
    if (!this.inFlight$) {
      this.inFlight$ = from(this.refreshWithCrossTabLock(startRefresh)).pipe(
        finalize(() => { this.inFlight$ = null; }),
        shareReplay(1),
      );
    }
    return this.inFlight$ as Observable<T>;
  }

  private refreshWithCrossTabLock<T>(startRefresh: () => Observable<T>): Promise<T | undefined> {
    const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
    if (!locks) {
      // No Web Locks API (very old browser) — falls back to the single-tab-only coalescing this
      // class always provided; cross-tab races remain possible there, same as before this change.
      return firstValueFrom(startRefresh());
    }

    return locks.request(TokenRefreshCoordinator.LOCK_NAME, async () => {
      const lastRefreshAt = Number(localStorage.getItem(TokenRefreshCoordinator.LAST_REFRESH_KEY) ?? 0);
      if (Date.now() - lastRefreshAt < TokenRefreshCoordinator.RECENT_REFRESH_WINDOW_MS) {
        // Another tab refreshed while we were waiting for the lock — nothing to do; the interceptor's
        // retry of the original request will succeed against the already-fresh cookie.
        return undefined;
      }

      const result = await firstValueFrom(startRefresh());
      localStorage.setItem(TokenRefreshCoordinator.LAST_REFRESH_KEY, String(Date.now()));
      return result;
    });
  }
}
