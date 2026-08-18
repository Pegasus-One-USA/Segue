import { WritableSignal } from '@angular/core';
import { Observable } from 'rxjs';
import { TerminologyImportHistoryEntry } from '../components/terminology-import-history/terminology-import-history.component';

const POLL_INTERVAL_MS = 3000;

/**
 * Extracted from the original `loinc-settings.component.ts`: fetches history, then keeps re-fetching every
 * 3s for as long as any entry is still `Running`, so a page doesn't have to hand-roll this loop itself.
 * Call `dispose()` from the owning component's `ngOnDestroy`.
 */
export class TerminologyHistoryPoller<T extends TerminologyImportHistoryEntry> {
  private timer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly fetchHistory: () => Observable<T[]>,
    private readonly history: WritableSignal<T[]>,
    private readonly loading: WritableSignal<boolean>,
    private readonly onError: () => void,
  ) {}

  load(): void {
    this.loading.set(true);
    this.fetchHistory().subscribe({
      next: (entries) => {
        this.history.set(entries);
        this.loading.set(false);
        clearTimeout(this.timer);
        if (entries.some((e) => e.status === 'Running')) {
          this.timer = setTimeout(() => this.load(), POLL_INTERVAL_MS);
        }
      },
      error: () => {
        this.loading.set(false);
        this.onError();
      },
    });
  }

  dispose(): void {
    clearTimeout(this.timer);
  }
}
