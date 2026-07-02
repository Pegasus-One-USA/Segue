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

  show(title: string, text: string, type: ToastType = 'info', durationMs?: number): void {
    if (this.timer) clearTimeout(this.timer);
    this.current.set({ title, text, type });
    this.timer = setTimeout(() => this.current.set(null), durationMs ?? DURATIONS[type]);
  }

  success(title: string, text: string): void { this.show(title, text, 'success'); }
  error(title: string, text: string): void   { this.show(title, text, 'error'); }
  warning(title: string, text: string): void { this.show(title, text, 'warning'); }
  info(title: string, text: string): void    { this.show(title, text, 'info'); }

  dismiss(): void {
    if (this.timer) clearTimeout(this.timer);
    this.current.set(null);
  }
}
