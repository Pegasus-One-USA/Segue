import { Injectable, signal } from '@angular/core';

// 'standard' — the original shell: topbar + footer, user menu top-right.
// 'focused' — Medplum-inspired: no topbar/footer, user menu anchored bottom-left of the sidebar,
// more vertical room for routed content.
export type LayoutMode = 'standard' | 'focused';

const STORAGE_KEY = 'fhirbridge.layout';

/** Mirrors ThemeService's shape exactly (localStorage-only, no server round-trip) — this is a
 *  per-browser chrome preference, not account data. */
@Injectable({ providedIn: 'root' })
export class LayoutService {
  readonly mode = signal<LayoutMode>(this.readStored());

  set(mode: LayoutMode): void {
    this.mode.set(mode);
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      /* storage unavailable (private mode) — non-fatal */
    }
  }

  private readStored(): LayoutMode {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      if (v === 'standard' || v === 'focused') return v;
      // Migrate a value stored under the old 'v1'/'v2' naming, from before this toggle had real names.
      if (v === 'v1') return 'standard';
      if (v === 'v2') return 'focused';
    } catch {
      /* ignore */
    }
    return 'standard';
  }
}
