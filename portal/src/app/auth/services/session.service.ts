import { Injectable, inject, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { Session } from '../models/auth-state.model';
import { AuthStore } from '../store/auth.store';
import { TokenService } from './token.service';

const IDLE_MS = 30 * 60 * 1000; // 30-minute idle timeout

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly store  = inject(AuthStore);
  private readonly tokens = inject(TokenService);
  private readonly router = inject(Router);
  private readonly zone   = inject(NgZone);

  private idleTimer?: ReturnType<typeof setTimeout>;

  // ─── Start session after login ─────────────────────────────────────────────
  start(userId: string, token: string, refreshToken: string, rememberMe = false): void {
    const expiresAt = new Date(Date.now() + 3600 * 1000);
    const session: Session = {
      userId, token, refreshToken, expiresAt,
      rememberMe, lastActivity: new Date(),
    };
    this.store.setSession(session);
    this.tokens.setTokens(token, refreshToken, rememberMe);
    this.resetIdleTimer();
  }

  // ─── End session on logout ─────────────────────────────────────────────────
  end(): void {
    this.store.clear();
    this.tokens.clearTokens();
    clearTimeout(this.idleTimer);
  }

  // ─── Touch activity on user interaction ───────────────────────────────────
  touch(): void {
    const s = this.store.session();
    if (s) {
      this.store.setSession({ ...s, lastActivity: new Date() });
      this.resetIdleTimer();
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
    this.end();
    this.store.setError('Your session has expired due to inactivity. Please sign in again.');
    this.router.navigate(['/auth/login']);
  }

  // ─── Restore session from storage ─────────────────────────────────────────
  restoreFromStorage(): boolean {
    const token = this.tokens.getAccessToken();
    if (!token || this.tokens.isExpired(token)) {
      this.tokens.clearTokens();
      return false;
    }
    return true;
  }
}
