import { Injectable, signal } from '@angular/core';
import { Observable, of } from 'rxjs';
import { SecurityEvent, SecurityEventType } from '../models/security-event.model';

const MAX_ATTEMPTS    = 5;
const LOCKOUT_MINUTES = 30;

interface AttemptRecord {
  count:       number;
  lockedUntil: Date | null;
}

export interface LockoutInfo {
  locked:           boolean;
  attemptsLeft:     number;
  minutesRemaining: number;
}

@Injectable({ providedIn: 'root' })
export class AccountSecurityService {

  private readonly attempts = new Map<string, AttemptRecord>();
  private readonly _events  = signal<SecurityEvent[]>([]);

  readonly securityEvents = this._events.asReadonly();

  // ── Lockout checks ─────────────────────────────────────────────────────────

  getLockoutInfo(email: string): LockoutInfo {
    const key    = email.toLowerCase();
    const record = this.attempts.get(key);

    if (!record) {
      return { locked: false, attemptsLeft: MAX_ATTEMPTS, minutesRemaining: 0 };
    }

    if (record.lockedUntil && record.lockedUntil > new Date()) {
      const ms = record.lockedUntil.getTime() - Date.now();
      return { locked: true, attemptsLeft: 0, minutesRemaining: Math.ceil(ms / 60000) };
    }

    // Lockout expired — clear it
    if (record.lockedUntil && record.lockedUntil <= new Date()) {
      this.attempts.delete(key);
      return { locked: false, attemptsLeft: MAX_ATTEMPTS, minutesRemaining: 0 };
    }

    return {
      locked:           false,
      attemptsLeft:     Math.max(0, MAX_ATTEMPTS - record.count),
      minutesRemaining: 0,
    };
  }

  isLocked(email: string): boolean {
    return this.getLockoutInfo(email).locked;
  }

  // ── Attempt recording ──────────────────────────────────────────────────────

  recordFailedAttempt(email: string, userId?: string): LockoutInfo {
    const key    = email.toLowerCase();
    const record = this.attempts.get(key) ?? { count: 0, lockedUntil: null };

    record.count++;

    if (record.count >= MAX_ATTEMPTS) {
      const lockedUntil = new Date(Date.now() + LOCKOUT_MINUTES * 60 * 1000);
      record.lockedUntil = lockedUntil;
      this.logEvent('account_locked', email, userId, `Locked for ${LOCKOUT_MINUTES} min after ${record.count} failed attempts`);
    } else {
      this.logEvent('login_failed', email, userId, `Failed attempt ${record.count} of ${MAX_ATTEMPTS}`);
    }

    this.attempts.set(key, record);

    return this.getLockoutInfo(email);
  }

  clearAttempts(email: string, userId?: string): void {
    const key = email.toLowerCase();
    if (this.attempts.has(key)) {
      this.attempts.delete(key);
      this.logEvent('login_success', email, userId);
    }
  }

  // ── Security event log ────────────────────────────────────────────────────

  logEvent(type: SecurityEventType, email: string, userId?: string, details?: string): void {
    const event: SecurityEvent = {
      id:        `sec-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
      type,
      email,
      userId,
      details,
      timestamp: new Date(),
    };
    this._events.update(list => [event, ...list]);
    console.info(`[SecurityService] ${type}`, email, details ?? '');
  }

  // ── Observable wrappers (for future HTTP replacement) ─────────────────────

  getSecurityEvents(): Observable<SecurityEvent[]> {
    return of(this._events());
  }
}
