import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { GlobalErrorDialogComponent, GlobalErrorDialogData } from './global-error-dialog.component';

/**
 * Phase 6A – opens the friendly-error dialog for a standardized backend error response. Guards against
 * stacking: if a global-error dialog is already open, subsequent errors are ignored until it's dismissed,
 * so a burst of failures doesn't bury the user in dialogs.
 */
@Injectable({ providedIn: 'root' })
export class GlobalErrorDialogService {
  private readonly dialog = inject(MatDialog);
  private isOpen = false;

  show(data: GlobalErrorDialogData): void {
    if (this.isOpen) { return; }
    this.isOpen = true;
    const ref = this.dialog.open(GlobalErrorDialogComponent, {
      data,
      autoFocus: false,
      restoreFocus: true,
      panelClass: 'global-error-dialog-panel',
    });
    ref.afterClosed().subscribe(() => (this.isOpen = false));
  }
}
