import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error' | 'warning' | 'info';

export interface Toast {
  title: string;
  text: string;
  type: ToastType;
}

const DURATIONS: Record<ToastType, number> = {
  success: 4000,
  info:    4000,
  warning: 6000,
  error:   8000,
};

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly current = signal<Toast | null>(null);

  private timer: ReturnType<typeof setTimeout> | null = null;
  // Set by SessionExpiredDialogService while the session-expired prompt is open. Every page's own
  // error handler still runs when a 401 reaches it (spinners need to stop, etc.), and many of them
  // call toast.error() with whatever message the backend sent back — which is often literally
  // "Your session has expired..." itself. Without this, that produces a dismissable toast stacked
  // behind/next-to the un-dismissable session dialog, on some pages but not others depending on
  // whether that page happens to have its own error handler. Suppressing every toast for the
  // duration keeps the one dialog as the single source of truth for "you've been logged out."
  private suppressed = false;

  show(title: string, text = '', type: ToastType = 'info', durationMs?: number): void {
    if (this.suppressed) return;
    if (this.timer) clearTimeout(this.timer);
    this.current.set({ title, text, type });
    this.timer = setTimeout(() => this.current.set(null), durationMs ?? DURATIONS[type]);
  }

  /** Hides any toast on screen and blocks new ones until resume() — see SessionExpiredDialogService. */
  suppress(): void {
    this.suppressed = true;
    this.dismiss();
  }

  resume(): void {
    this.suppressed = false;
  }

  // Two call shapes are supported so both the existing (title, text) call sites and the migrated
  // single-message ones read cleanly:
  //   toast.success('User "X" deleted.')            → title only
  //   toast.success('Saved', 'Your changes are live') → title + supporting text
  success(title: string, text = ''): void { this.show(title, text, 'success'); }
  error(title: string, text = ''): void   { this.show(title, text, 'error'); }
  warning(title: string, text = ''): void { this.show(title, text, 'warning'); }
  info(title: string, text = ''): void    { this.show(title, text, 'info'); }

  dismiss(): void {
    if (this.timer) clearTimeout(this.timer);
    this.current.set(null);
  }
}
