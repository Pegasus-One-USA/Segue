import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, map, of, switchMap } from 'rxjs';
import { ConfirmDialogComponent } from '../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { HasUnsavedChanges } from '../guards/has-unsaved-changes';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';
import { UnsavedChangesRegistryService } from './unsaved-changes-registry.service';

/**
 * Shared "leave this page?" prompt used by unsaved-changes.guard.ts for every route that carries
 * in-app editable state. Centralized so every page asks the same way instead of each component
 * wiring its own MatDialog call.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesPromptService {
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(ToastService);
  private readonly authStore = inject(AuthStore);
  private readonly registry = inject(UnsavedChangesRegistryService);

  /** Resolves true if navigation should proceed. */
  confirmLeave(component: HasUnsavedChanges): Observable<boolean> {
    // Once the session itself has already ended (SessionService.onIdle()'s 30-minute inactivity
    // timeout, or authInterceptor's forced logout on a failed token refresh — both clear AuthStore
    // BEFORE navigating to /auth/login), there's nothing left to meaningfully save: the backend
    // will 401 any further write regardless. Without this check, the MatDialog opened below never
    // resolves on its own (afterClosed() only emits when a human clicks a button), which silently
    // traps an inactive user's tab on the current screen instead of redirecting them — defeating
    // the very timeout this is supposed to enforce.
    if (!this.authStore.isAuthenticated()) {
      return of(true);
    }

    if (component.isSaveInProgress?.()) {
      this.toast.warning('Please wait for the current save to finish before leaving this page.');
      return of(false);
    }

    if (!component.hasUnsavedChanges()) {
      return of(true);
    }

    // A route can own its own inline "leave this page?" modal (see confirmLeaveDialog's doc
    // comment) instead of the generic MatDialog below — e.g. Workflow Builder renders one matching
    // destination-wizard's in-canvas "Exit mapping" confirm exactly, rather than fighting
    // MatDialog's own surface/padding defaults with CSS overrides. Every route that doesn't
    // implement it keeps getting the exact same MatDialog as before.
    if (component.confirmLeaveDialog) {
      return component.confirmLeaveDialog();
    }

    return this.confirmDiscard();
  }

  /**
   * Leaving the whole PAGE (route change, sign out): the page itself, AND anything open on it that registered
   * its own check — e.g. the mapping popover, whose edits live only in it until its Save, so the page reads
   * clean. confirmLeave above stays scoped to the one component it is given, because it also guards closing
   * a dialog or a form (DialogService, the EHR source form's Cancel): asking the registry there prompted for
   * some other component's edits. A save in flight anywhere blocks with "please wait", never "Leave".
   */
  confirmLeavePage(page: HasUnsavedChanges): Observable<boolean> {
    if (!this.authStore.isAuthenticated()) {
      return of(true);
    }
    if (page.isSaveInProgress?.() || this.registry.isAnySaveInProgress()) {
      this.toast.warning('Please wait for the current save to finish before leaving this page.');
      return of(false);
    }
    if (!page.hasUnsavedChanges() && !this.registry.hasAnyUnsavedEdits()) {
      return of(true);
    }
    if (page.confirmLeaveDialog) {
      return page.confirmLeaveDialog();
    }
    // Only the page's OWN edits can be saved from here: when the dirt is solely in something registered on it (a
    // popover with its own Save), the plain Leave/Cancel prompt stands.
    if (page.saveBeforeLeave && page.hasUnsavedChanges()) {
      const saveBeforeLeave = () => page.saveBeforeLeave!();
      return this.openLeavePrompt({
        message: 'Page data has been modified. Do you want to save your changes before leaving?',
        saveLabel: 'Save',
      }).pipe(switchMap(choice => choice === 'save' ? saveBeforeLeave() : of(choice === true)));
    }
    return this.confirmDiscard();
  }

  /** The "Leave this page?" modal itself. True = Leave (discard), false = Cancel (stay). */
  confirmDiscard(): Observable<boolean> {
    return this.openLeavePrompt({}).pipe(map(result => result === true));
  }

  /** Same modal for both prompts; `extra` only ever adds the Save button (and its message) on top of the defaults. */
  private openLeavePrompt(extra: { message?: string; saveLabel?: string }): Observable<boolean | 'save' | undefined> {
    return this.dialog
      .open<ConfirmDialogComponent, unknown, boolean | 'save'>(ConfirmDialogComponent, {
        width: '440px',
        restoreFocus: false,
        // Lifts the CDK overlay container above GlobalLoaderComponent (see styles.scss). This prompt
        // opens while the loader is already up — NavigationStart raised it, and answering this is what
        // lets navigation resolve and stop it — so under the loader its own buttons are unclickable and
        // the page deadlocks on the spinner.
        panelClass: 'leave-confirm-dialog',
        data: {
          title: 'Leave this page?',
          message: 'You have unsaved changes that will be lost if you leave. Continue?',
          confirmLabel: 'Leave',
          danger: true,
          ...extra,
        },
      })
      .afterClosed();
  }
}
