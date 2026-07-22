import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, map, of } from 'rxjs';
import { ConfirmDialogComponent } from '../../user-management/dialogs/confirm-dialog/confirm-dialog.component';
import { HasUnsavedChanges } from '../guards/has-unsaved-changes';

/**
 * Shared "leave this page?" prompt used by unsaved-changes.guard.ts for every route that carries
 * in-app editable state. Centralized so every page asks the same way instead of each component
 * wiring its own MatDialog call.
 */
@Injectable({ providedIn: 'root' })
export class UnsavedChangesPromptService {
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  /** Resolves true if navigation should proceed. */
  confirmLeave(component: HasUnsavedChanges): Observable<boolean> {
    if (component.isSaveInProgress?.()) {
      this.snack.open('Please wait for the current save to finish before leaving this page.', 'Dismiss', {
        duration: 4000,
      });
      return of(false);
    }

    if (!component.hasUnsavedChanges()) {
      return of(true);
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
