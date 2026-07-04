import { Injectable, signal } from '@angular/core';

// Add new theme ids here (e.g. 'ocean'), then add a matching
// `html[data-theme='ocean']` block in styles.scss — no other code changes needed.
export type ThemeMode = 'light' | 'dark' | 'system';

/** A concretely-applied theme (everything except the 'system' indirection). */
export type AppliedTheme = Exclude<ThemeMode, 'system'>;

const STORAGE_KEY = 'fhirbridge.theme';

/**
 * Applies the design-token theme by toggling `data-theme="dark"` on <html>.
 * Only the semantic token tier is themed (see docs/design-token-architecture.md);
 * everything downstream cascades automatically.
 *
 * Default mode is 'light' to preserve current behavior — dark is opt-in.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  /** User selection: explicit light/dark, or follow the OS ('system'). */
  readonly mode = signal<ThemeMode>(this.readStored());

  /** The theme actually applied, after resolving 'system'. */
  readonly resolved = signal<AppliedTheme>('light');

  private readonly media =
    typeof window !== 'undefined' && window.matchMedia
      ? window.matchMedia('(prefers-color-scheme: dark)')
      : null;

  constructor() {
    // Re-resolve when the OS theme changes, but only while following 'system'.
    this.media?.addEventListener('change', () => {
      if (this.mode() === 'system') this.apply();
    });
    this.apply();
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      /* storage unavailable (private mode) — non-fatal */
    }
    this.apply();
  }

  /** Flip between light and dark based on what is currently showing. */
  toggle(): void {
    this.set(this.resolved() === 'dark' ? 'light' : 'dark');
  }

  private apply(): void {
    if (typeof document === 'undefined') return;
    const mode = this.mode();
    // 'system' resolves via prefers-color-scheme; any other mode applies directly.
    const applied: AppliedTheme =
      mode === 'system' ? (this.media?.matches ? 'dark' : 'light') : mode;
    this.resolved.set(applied);
    const root = document.documentElement;
    // 'light' is the :root default (no attribute); every other theme is an attribute.
    if (applied === 'light') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', applied);
  }

  private readStored(): ThemeMode {
    try {
      const v = localStorage.getItem(STORAGE_KEY) as ThemeMode | null;
      if (v === 'light' || v === 'dark' || v === 'system') return v;
    } catch {
      /* ignore */
    }
    return 'light';
  }
}
