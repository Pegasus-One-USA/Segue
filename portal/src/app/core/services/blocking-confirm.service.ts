import { Injectable, computed, signal } from '@angular/core';

/**
 * Tracks whether a route is currently showing its OWN in-template confirm dialog that the app is
 * waiting on — specifically a CanDeactivate "leave this page?" prompt (see
 * HasUnsavedChanges.confirmLeaveDialog).
 *
 * Why this exists: AppComponent marks the whole routed subtree [inert] while LoadingService reports
 * busy, and a router navigation counts as busy from NavigationStart until one of
 * NavigationEnd/Cancel/Error/Skipped. A CanDeactivate guard runs *between* those two points, so while
 * such a dialog is open the app is busy by definition — and an in-template dialog (unlike a MatDialog,
 * which renders into .cdk-overlay-container, outside the routed subtree) sits inside that inert
 * subtree. inert removes its whole subtree from hit-testing, which no z-index can override, so the
 * dialog's own buttons stop responding: the navigation can't finish until the dialog is answered, and
 * the dialog can't be answered until the navigation finishes. AppComponent consults this to suppress
 * [inert] for exactly that window.
 */
@Injectable({ providedIn: 'root' })
export class BlockingConfirmService {
  private readonly openCount = signal(0);

  /** True while at least one in-template blocking confirm is awaiting the user's answer. */
  readonly isOpen = computed(() => this.openCount() > 0);

  open(): void {
    this.openCount.update(n => n + 1);
  }

  close(): void {
    this.openCount.update(n => Math.max(0, n - 1));
  }
}
