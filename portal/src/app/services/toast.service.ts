import { Injectable, signal } from '@angular/core';

export interface Toast {
  title: string;
  text: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly current = signal<Toast | null>(null);

  private timer: ReturnType<typeof setTimeout> | null = null;

  show(title: string, text: string, durationMs = 2600): void {
    if (this.timer) clearTimeout(this.timer);
    this.current.set({ title, text });
    this.timer = setTimeout(() => this.current.set(null), durationMs);
  }

  dismiss(): void {
    if (this.timer) clearTimeout(this.timer);
    this.current.set(null);
  }
}
