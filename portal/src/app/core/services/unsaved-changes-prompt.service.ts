import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, map, of } from 'rxjs';
import { ConfirmDialogComponent } from '../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { HasUnsavedChanges } from '../guards/has-unsaved-changes';
import { ToastService } from '../../services/toast.service';
import { AuthStore } from '../../auth/store/auth.store';

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

    return this.dialog
      .open(ConfirmDialogComponent, {
        width: '440px',
        restoreFocus: false,
        data: {
          title: 'Leave this page?',
          message: 'You have unsaved changes that will be lost if you leave. Continue?',
          confirmLabel: 'Leave',
          danger: true,
        },
      })
      .afterClosed()
      .pipe(map(result => result === true));
  }
}
