import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { InviteResult } from '../../../auth/models/user.model';

@Component({
  selector: 'app-invite-result-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule],
  templateUrl: './invite-result-dialog.component.html',
  styleUrls: ['./invite-result-dialog.component.scss'],
})
export class InviteResultDialogComponent {
  readonly dialogRef = inject(MatDialogRef<InviteResultDialogComponent>);
  readonly data: InviteResult = inject(MAT_DIALOG_DATA);
  private readonly snackBar = inject(MatSnackBar);

  copied = signal(false);

  /** Text shown / copied: prefer the absolute link, otherwise the raw token. */
  get shareValue(): string {
    return this.data.invitationLink ?? this.data.invitationToken ?? '';
  }

  copy(): void {
    const value = this.shareValue;
    if (!value) return;
    navigator.clipboard?.writeText(value).then(
      () => {
        this.copied.set(true);
        this.snackBar.open('Invitation link copied to clipboard.', 'Dismiss', { duration: 2500 });
        setTimeout(() => this.copied.set(false), 2000);
      },
      () => this.snackBar.open('Could not copy. Select and copy manually.', 'Dismiss', { duration: 3000 }),
    );
  }

  close(): void {
    this.dialogRef.close();
  }
}
