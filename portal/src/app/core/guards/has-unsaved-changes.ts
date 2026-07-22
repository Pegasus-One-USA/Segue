// Implemented by any routed component that can be navigated away from mid-edit or mid-save.
// unsaved-changes.guard.ts consults this on every attempted navigation away from the route.
export interface HasUnsavedChanges {
  /** True if there are edits that haven't been persisted yet. */
  hasUnsavedChanges(): boolean;

  /** True while a save/update request is actually in flight. Navigation is blocked outright in
   *  this case (not just confirmed) — the in-flight request isn't cancellable and may be
   *  provisioning server-side records that a partial navigation would leave orphaned. */
  isSaveInProgress?(): boolean;
}
