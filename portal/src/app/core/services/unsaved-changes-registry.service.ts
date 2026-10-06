import { DestroyRef, Injectable, inject } from '@angular/core';

/**
 * Tracks every currently-mounted component that has unsaved/in-flight state, so a single
 * app-shell `beforeunload` listener can cover the whole app without each page owning its own
 * listener. Complements unsaved-changes.guard.ts, which only fires on in-app route navigation —
 * this also catches tab close, refresh, and browser history moving outside the SPA entirely.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesRegistryService {
  private readonly entries = new Set<{ isDirty: () => boolean; isSaving?: () => boolean }>();

  /**
   * Registers a check and auto-unregisters it when the calling component is destroyed. Call
   * from a component field initializer or constructor (an active injection context) — mirrors
   * the same default-parameter pattern Angular's own `takeUntilDestroyed()` uses.
   */
  register(isDirty: () => boolean, destroyRef: DestroyRef = inject(DestroyRef), isSaving?: () => boolean): void {
    // `isSaving` is separate from `isDirty` so leaving can tell "unsaved edits" (ask) from "a save still in
    // flight" (wait) — a single combined check made sign out offer "Leave" in the middle of a save.
    const entry = { isDirty, isSaving };
    this.entries.add(entry);
    destroyRef.onDestroy(() => this.entries.delete(entry));
  }

  /** Unsaved edits OR a save in flight — either way the tab should warn before closing (app-shell's
   *  beforeunload). */
  hasAnyUnsavedChanges(): boolean {
    return this.hasAnyUnsavedEdits() || this.isAnySaveInProgress();
  }

  hasAnyUnsavedEdits(): boolean {
    for (const entry of this.entries) {
      if (entry.isDirty()) return true;
    }
    return false;
  }

  isAnySaveInProgress(): boolean {
    for (const entry of this.entries) {
      if (entry.isSaving?.()) return true;
    }
    return false;
  }
}
