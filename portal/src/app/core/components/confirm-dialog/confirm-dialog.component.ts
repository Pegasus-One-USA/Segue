import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../services/dialog.service';

export interface ConfirmDialogData {
  title: string;
  message: string;
  confirmLabel?: string;
  danger?: boolean;
}

/**
 * The DialogService-based twin of user-management/dialogs/confirm-dialog/confirm-dialog.component.ts
 * — same markup/styling, different DI tokens (DIALOG_DATA/DialogRef instead of
 * MAT_DIALOG_DATA/MatDialogRef). Kept as a SEPARATE component rather than migrating the original in
 * place: UnsavedChangesPromptService opens the original via real MatDialog for its own "Leave this
 * page?" prompt, and DialogService itself depends on UnsavedChangesPromptService (for
 * DialogRef.attemptClose()'s dirty-gated close) — migrating the shared original would make
 * UnsavedChangesPromptService depend on DialogService too, a circular dependency Angular's DI cannot
 * resolve. Every OTHER caller (delete/deactivate/regenerate confirmations across the list pages) uses
 * this one instead, going forward.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './confirm-dialog.component.html',
  styleUrls: ['./confirm-dialog.component.scss'],
})
export class ConfirmDialogComponent {
  readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  readonly data: ConfirmDialogData = inject(DIALOG_DATA) as ConfirmDialogData;
}
