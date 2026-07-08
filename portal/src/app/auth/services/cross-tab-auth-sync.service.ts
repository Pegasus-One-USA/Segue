import { Injectable, inject, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { AuthStore } from '../store/auth.store';
import { TokenService, ACCESS_KEY } from './token.service';
import { buildUserFromJwt } from './jwt-user.mapper';

/**
 * Tokens live in Web Storage specifically so a "Remember me" session survives a
 * restart — a side effect we get for free is that writing to localStorage fires a
 * native `storage` event in every OTHER tab on the same origin. This service listens
 * for that event so logging out (or a silent token refresh picking up new permission
 * claims) in one tab is reflected in every other open tab immediately, instead of
 * them running with stale identity until their next request happens to 401.
 *
 * Honest limitation: sessionStorage (used whenever "Remember me" is unchecked) is
 * tab-scoped by browser design and never fires cross-tab storage events. There is no
 * API to synchronize those sessions — that's correct browser behavior, not a gap.
 */
@Injectable({ providedIn: 'root' })
export class CrossTabAuthSyncService {
  private readonly store  = inject(AuthStore);
  private readonly tokens = inject(TokenService);
  private readonly router = inject(Router);
  private readonly zone   = inject(NgZone);

  constructor() {
    if (typeof window === 'undefined') return;
    window.addEventListener('storage', (event) => this.zone.run(() => this.onStorageEvent(event)));
  }

  private onStorageEvent(event: StorageEvent): void {
    if (event.key !== ACCESS_KEY || event.storageArea !== localStorage) return;

    if (!event.newValue) {
      // Another tab logged out (or cleared its tokens). Mirror it locally — don't call
      // the logout API again, that tab already did.
      this.store.clear();
      this.router.navigate(['/auth/login']);
      return;
    }

    // Another tab logged in, or silently refreshed, with a new access token. Adopt its
    // claims here too so this tab's permissions match immediately.
    const payload = this.tokens.decodePayload<Record<string, unknown>>(event.newValue);
    if (payload) this.store.setUser(buildUserFromJwt(payload));
  }
}
