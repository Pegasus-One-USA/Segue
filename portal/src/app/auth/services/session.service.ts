import { Injectable, inject, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, finalize, EMPTY } from 'rxjs';
import { Session } from '../models/auth-state.model';
import { AuthStore } from '../store/auth.store';
import { TokenService } from './token.service';
import { IAuthService } from './i-auth.service';
import { MappingSnapshotService } from '../../components/node-library-v2/destination-wizard/field-mapping/mapping-snapshot.service';
import { SessionExpiredDialogService } from './session-expired-dialog.service';

 const IDLE_MS = 30 * 60 * 1000; // 30-minute idle timeout
// Deliberately excludes 'mousemove' — that fires dozens of times a second while the user's hand merely
// rests near the mouse, far too noisy a signal for "the user is actively working" even throttled.
// Includes 'pointerdown'/'wheel' alongside the legacy 'mousedown'/'touchstart'/'scroll': the workflow
// and field-mapping canvases drive node dragging, panning and zoom entirely off Pointer Events, and a
// canvas that calls preventDefault() on its own pointerdown (several do, to suppress touch scrolling/
// text-selection while dragging) also suppresses the browser's synthetic 'mousedown' compatibility
// event — so a user who spends 30+ minutes only dragging/connecting/zooming nodes, never touching a
// button or a keyboard, would otherwise silently register as idle. These seven cover every real
// interaction without needing to throttle a firehose event.
const ACTIVITY_EVENTS = ['mousedown', 'pointerdown', 'keydown', 'touchstart', 'scroll', 'wheel', 'click'] as const;
// Re-touch at most once a minute — plenty to keep the idle timer perpetually reset during any real
// activity (a 60s cadence against a 30-minute timeout has enormous margin) without spamming an AuthStore
// write (and the resulting resetIdleTimer() churn) on every single click/keystroke.
const ACTIVITY_THROTTLE_MS = 60 * 1000;

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly store  = inject(AuthStore);
  private readonly tokens = inject(TokenService);
  private readonly router = inject(Router);
  private readonly zone   = inject(NgZone);
  private readonly mappingSnapshots = inject(MappingSnapshotService);
  private readonly authApi = inject(IAuthService);
  private readonly sessionExpiredDialog = inject(SessionExpiredDialogService);

  private idleTimer?: ReturnType<typeof setTimeout>;
  private activityListenersActive = false;
  private lastTouchAt = 0;

  // ─── Start session after login ─────────────────────────────────────────────
  // HIPAA #7: no raw token to carry here anymore — it's an HttpOnly cookie the backend already
  // set. `Session.token`/`refreshToken` are kept as empty strings purely so existing consumers of
  // the `Session` shape don't need touching; nothing reads them for authentication anymore.
  //
  // `rememberMe` is recorded on the Session object below purely as an in-memory UI-facing mirror of
  // the choice already sent to (and now actually acted on by) the backend — it is NOT read back to
  // make any client-side decision. The server, via whether the refresh/CSRF cookies it set carry an
  // Expires attribute, is the sole source of truth for whether this session survives a browser
  // restart (see AuthController.IssueTokenCookiesAndStrip); this client never re-derives that itself.
  start(userId: string, rememberMe = false): void {
    const expiresAt = new Date(Date.now() + 3600 * 1000);
    const session: Session = {
      userId, token: '', refreshToken: '', expiresAt,
      rememberMe, lastActivity: new Date(),
    };
    this.store.setSession(session);
    this.tokens.markSessionActive();
    this.resetIdleTimer();
    this.startActivityListeners();
  }

  // ─── End session on logout ─────────────────────────────────────────────────
  end(): void {
    this.store.clear();
    this.tokens.clearTokens();
    this.mappingSnapshots.clearAll();
    clearTimeout(this.idleTimer);
    this.stopActivityListeners();
  }

  // ─── Touch activity on user interaction ───────────────────────────────────
  touch(): void {
    const s = this.store.session();
    if (s) {
      this.store.setSession({ ...s, lastActivity: new Date() });
      this.resetIdleTimer();
    }
  }

  // ─── Global activity listeners ─────────────────────────────────────────────
  // touch() above always existed, but had zero callers anywhere in the app — no activity listener, no
  // HTTP hook, nothing — so resetIdleTimer() only ever ran once, at login. In practice that turned the
  // "30-minute idle timeout" into a flat 30-minute session cap regardless of activity, logging active
  // users out mid-task well before either the JWT (60/480 min) or the refresh token (30 days) would ever
  // expire. Wiring real DOM activity to touch() here is what makes it genuinely inactivity-based.
  private readonly onActivityEvent = (): void => {
    const now = Date.now();
    if (now - this.lastTouchAt < ACTIVITY_THROTTLE_MS) return;
    this.lastTouchAt = now;
    this.zone.run(() => this.touch());
  };

  private startActivityListeners(): void {
    if (this.activityListenersActive || typeof document === 'undefined') return;
    this.activityListenersActive = true;
    // Attached outside the Angular zone — these fire on every click/keystroke/scroll, and the throttle
    // check above (which decides whether to actually touch()) shouldn't itself trigger change detection
    // on the 59 out of 60 seconds it's a no-op.
    this.zone.runOutsideAngular(() => {
      for (const evt of ACTIVITY_EVENTS) {
        document.addEventListener(evt, this.onActivityEvent, { passive: true });
      }
    });
  }

  private stopActivityListeners(): void {
    if (!this.activityListenersActive || typeof document === 'undefined') return;
    this.activityListenersActive = false;
    for (const evt of ACTIVITY_EVENTS) {
      document.removeEventListener(evt, this.onActivityEvent);
    }
  }

  // ─── Idle timer ───────────────────────────────────────────────────────────
  private resetIdleTimer(): void {
    clearTimeout(this.idleTimer);
    this.zone.runOutsideAngular(() => {
      this.idleTimer = setTimeout(() => {
        this.zone.run(() => this.onIdle());
      }, IDLE_MS);
    });
  }

  private onIdle(): void {
    // Revoke the refresh token + clear the HttpOnly access/refresh/CSRF cookies server-side — the
    // exact same POST /auth/logout call AuthService.logout() makes for a manual logout. Without
    // this, end() below only ever clears THIS tab's in-memory/localStorage state; the still-valid
    // HttpOnly cookies would silently re-authenticate the user via initFromToken()'s GET /auth/me
    // on the very next page load/refresh, undoing the inactivity logout entirely. catchError+finalize
    // mirrors AuthService.logout() exactly, so a failed/offline logout call still redirects rather
    // than leaving the user stuck.
    this.authApi.logout().pipe(
      catchError(() => EMPTY),
      finalize(() => {
        this.end();
        this.store.setError('Your session has expired due to inactivity. Please sign in again.');
        // Unlike the interceptor's 401 path (user is actively at the keyboard, so the prompt can sit
        // over whatever they were looking at), idle timeout exists specifically FOR the unattended
        // workstation — showing the prompt without navigating first would leave PHI (execution history
        // rows, field-lineage values, mapping previews) rendered behind the dialog's translucent
        // backdrop for as long as the browser stays open on a shared clinical machine. Navigate away
        // first so the routed content is actually gone, then show the same unified prompt over the now-
        // blank login page.
        void this.router.navigate(['/auth/login']).then(() => {
          this.sessionExpiredDialog.show(
            'Your session has expired due to inactivity. Please log in again to continue.', true);
        });
      }),
    ).subscribe();
  }

  // ─── Restore session from storage ─────────────────────────────────────────
  // HIPAA #7: there's no client-readable token left to inspect for expiry — hasSession() is a
  // best-effort local hint (the CSRF cookie's presence); the server's HttpOnly cookie is the real
  // authority, and a request against an actually-expired session will 401 and route through the
  // interceptor's refresh-or-logout path same as any other request.
  restoreFromStorage(): boolean {
    if (!this.tokens.hasSession()) {
      this.tokens.clearTokens();
      return false;
    }
    // A page reload creates a brand-new SessionService instance (providedIn: 'root' still means one per
    // app bootstrap, not one for the browser tab's whole lifetime) — without re-arming these here, the
    // idle timer and its activity listeners would just never start again after any refresh, silently
    // disabling the whole inactivity timeout for the rest of that tab's life.
    this.resetIdleTimer();
    this.startActivityListeners();
    return true;
  }
}
