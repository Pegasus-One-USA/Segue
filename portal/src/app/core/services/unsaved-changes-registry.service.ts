import { DestroyRef, Injectable, inject } from '@angular/core';

/**
 * Tracks every currently-mounted component that has unsaved/in-flight state, so a single
 * app-shell `beforeunload` listener can cover the whole app without each page owning its own
 * listener. Complements unsaved-changes.guard.ts, which only fires on in-app route navigation —
 * this also catches tab close, refresh, and browser history moving outside the SPA entirely.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesRegistryService {
  private readonly checks = new Set<() => boolean>();

  /**
   * Registers a check and auto-unregisters it when the calling component is destroyed. Call
   * from a component field initializer or constructor (an active injection context) — mirrors
   * the same default-parameter pattern Angular's own `takeUntilDestroyed()` uses.
   */
  register(check: () => boolean, destroyRef: DestroyRef = inject(DestroyRef)): void {
    this.checks.add(check);
    destroyRef.onDestroy(() => this.checks.delete(check));
  }

  hasAnyUnsavedChanges(): boolean {
    for (const check of this.checks) {
      if (check()) return true;
    }
    return false;
  }
}
