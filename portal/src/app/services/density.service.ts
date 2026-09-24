import { Injectable, signal } from '@angular/core';

// 'normal' — the original spacing/type scale everywhere. 'compact' shrinks table rows, cards, and
// page headers app-wide via [data-density="compact"] in styles.scss — nothing else changes.
export type DensityMode = 'normal' | 'compact';

const STORAGE_KEY = 'fhirbridge.density';

/** Mirrors ThemeService/LayoutService exactly (localStorage-only, no server round-trip) — a
 *  per-browser display preference, not account data. */
@Injectable({ providedIn: 'root' })
export class DensityService {
  readonly mode = signal<DensityMode>(this.readStored());

  constructor() {
    this.apply(this.mode());
  }

  set(mode: DensityMode): void {
    this.mode.set(mode);
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      /* storage unavailable (private mode) — non-fatal */
    }
    this.apply(mode);
  }

  private apply(mode: DensityMode): void {
    if (typeof document === 'undefined') return;
    const root = document.documentElement;
    // 'normal' is the :root default (no attribute); 'compact' is an attribute, same pattern
    // ThemeService uses for 'light' vs every other theme.
    if (mode === 'compact') root.setAttribute('data-density', 'compact');
    else root.removeAttribute('data-density');
  }

  private readStored(): DensityMode {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      if (v === 'normal' || v === 'compact') return v;
    } catch {
      /* ignore */
    }
    return 'normal';
  }
}
