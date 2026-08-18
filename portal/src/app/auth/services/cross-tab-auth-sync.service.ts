import { Injectable, inject, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { SESSION_MARKER_KEY } from './token.service';
import { AuthService } from './auth.service';

/**
 * The tokens themselves live in HttpOnly cookies now (HIPAA #7), invisible to this or any other
 * tab's JS — but a lightweight, non-sensitive session marker is still written to localStorage on
 * login/logout purely to fire a native `storage` event in every OTHER tab on the same origin. This
 * service listens for that event so logging out in one tab is reflected in every other open tab
 * immediately, instead of them running with stale identity until their next request 401s. Since
 * the marker carries no claims itself, a fresh login in another tab is picked up via a real
 * `GET /auth/me` call rather than decoding anything out of the event.
 *
 * Honest limitation: sessionStorage-scoped ("Remember me" unchecked) sessions are tab-scoped by
 * browser design and never fire cross-tab storage events. There is no API to synchronize those —
 * that's correct browser behavior, not a gap.
 */
@Injectable({ providedIn: 'root' })
export class CrossTabAuthSyncService {
  private readonly store  = inject(AuthStore);
  private readonly auth   = inject(AuthService);
  private readonly router = inject(Router);
  private readonly zone   = inject(NgZone);

  constructor() {
    if (typeof window === 'undefined') return;
    window.addEventListener('storage', (event) => this.zone.run(() => this.onStorageEvent(event)));
  }

  private onStorageEvent(event: StorageEvent): void {
    if (event.key !== SESSION_MARKER_KEY || event.storageArea !== localStorage) return;

    if (!event.newValue) {
      // Another tab logged out (or cleared its session marker). Mirror it locally — don't call
      // the logout API again, that tab already did.
      this.store.clear();
      this.router.navigate(['/auth/login']);
      return;
    }

    // Another tab logged in, or silently refreshed. Fetch the current profile here too so this
    // tab's permissions match immediately, rather than waiting for its next request to 401.
    this.auth.getCurrentUser().subscribe({
      next: user => this.store.setUser(user),
      error: () => { /* not authenticated in this tab's cookie jar (shouldn't happen, same-origin) */ },
    });
  }
}
