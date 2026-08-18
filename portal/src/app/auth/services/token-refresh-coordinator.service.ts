import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { finalize, shareReplay } from 'rxjs/operators';

/**
 * Coalesces concurrent refresh attempts into one in-flight request.
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
 * HIPAA #7: the refresh call itself no longer takes/returns a raw token (both now live
 * in HttpOnly cookies) — genericized to `T` (the rebuilt `User`) so the coordinator no
 * longer needs to know the token shape at all.
 */
@Injectable({ providedIn: 'root' })
export class TokenRefreshCoordinator {
  private inFlight$: Observable<unknown> | null = null;

  refresh<T>(startRefresh: () => Observable<T>): Observable<T> {
    if (!this.inFlight$) {
      this.inFlight$ = startRefresh().pipe(
        finalize(() => { this.inFlight$ = null; }),
        shareReplay(1),
      );
    }
    return this.inFlight$ as Observable<T>;
  }
}
