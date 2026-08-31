// Implemented by any routed component that can be navigated away from mid-edit or mid-save.
// unsaved-changes.guard.ts consults this on every attempted navigation away from the route.
import { Observable } from 'rxjs';

export interface HasUnsavedChanges {
  /** True if there are edits that haven't been persisted yet. */
  hasUnsavedChanges(): boolean;

  /** True while a save/update request is actually in flight. Navigation is blocked outright in
   *  this case (not just confirmed) — the in-flight request isn't cancellable and may be
   *  provisioning server-side records that a partial navigation would leave orphaned. */
  isSaveInProgress?(): boolean;

  /** Lets a route render its own inline "leave this page?" confirm modal instead of the generic
   *  MatDialog unsaved-changes-prompt.service.ts opens by default — e.g. Workflow Builder uses
   *  this to match destination-wizard's in-canvas "Exit mapping" confirm exactly (same markup
   *  pattern, not a MatDialog). Must resolve true to allow navigation, false to stay. Every route
   *  that doesn't implement this keeps getting the shared default dialog, unchanged. */
  confirmLeaveDialog?(): Observable<boolean>;
}
