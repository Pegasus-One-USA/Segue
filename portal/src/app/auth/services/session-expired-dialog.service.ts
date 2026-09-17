import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, finalize, EMPTY } from 'rxjs';
import Swal from 'sweetalert2';
import { ToastService } from '../../services/toast.service';
import { IAuthService } from './i-auth.service';
import { TokenService } from './token.service';
import { AuthStore } from '../store/auth.store';

const DEFAULT_MESSAGE = 'Your session has expired. Please log in again to continue.';

/**
 * The single place in the app that triggers the session-expired prompt — called from the auth
 * interceptor (401 that can't be silently refreshed) and from SessionService (idle timeout), so
 * every path that ends a session shows the exact same prompt regardless of which page the user
 * was on.
 *
 * Uses SweetAlert2 rather than Angular Material's MatDialog: SweetAlert2 renders its own
 * self-contained overlay (a single fixed element with its own backdrop) instead of relying on
 * Angular CDK's overlay/z-index stack, which was fighting GlobalLoaderComponent's busy-spinner
 * overlay for stacking order and bleeding through at the edges.
 */
@Injectable({ providedIn: 'root' })
export class SessionExpiredDialogService {
  private readonly router  = inject(Router);
  private readonly toast   = inject(ToastService);
  // IAuthService (not AuthService) deliberately: AuthService injects SessionService, which injects
  // this service — injecting AuthService back here would be a circular DI dependency. IAuthService's
  // concrete implementation only depends on HttpClient, same as SessionService.onIdle() already uses
  // it for exactly this reason.
  private readonly authApi = inject(IAuthService);
  private readonly tokens  = inject(TokenService);
  private readonly store   = inject(AuthStore);
  private isOpen = false;

  show(message: string = DEFAULT_MESSAGE): void {
    if (this.isOpen) { return; }
    this.isOpen = true;
    // Every page that has its own error handler on the failing request still runs it (spinners
    // stop, etc.) and many of them toast whatever message the backend sent back for the 401 — often
    // this exact "session has expired" text. Silence those while the prompt owns the message.
    this.toast.suppress();

    void Swal.fire({
      icon: 'warning',
      title: 'Session expired',
      text: message,
      confirmButtonText: 'Log In',
      // The session really is gone — the only way out is the button, not a backdrop click or Escape.
      allowOutsideClick: false,
      allowEscapeKey: false,
      showCancelButton: false,
      showCloseButton: false,
      customClass: {
        popup: 'se-swal-popup',
        title: 'se-swal-title',
        htmlContainer: 'se-swal-text',
        confirmButton: 'se-swal-confirm',
        icon: 'se-swal-icon',
      },
      buttonsStyling: false,
    }).then(() => this.logOutAndRedirect());
  }

  /** Runs the real logout call — not just a client-side navigate — so the HttpOnly session/refresh
   *  cookies are actually revoked server-side (TokenService.clearTokens() only ever clears the local
   *  session marker, never the cookies themselves; only POST /auth/logout's response does that). */
  private logOutAndRedirect(): void {
    this.authApi.logout().pipe(
      // A session that's already gone 401s on its own logout call — swallow it, cleanup below still
      // runs unconditionally via finalize(), same pattern as AuthService.logout()/SessionService.onIdle().
      catchError(() => EMPTY),
      finalize(() => {
        this.store.clear();
        this.tokens.clearTokens();
        this.isOpen = false;
        this.toast.resume();
        void this.router.navigate(['/auth/login']);
      }),
    ).subscribe();
  }
}
