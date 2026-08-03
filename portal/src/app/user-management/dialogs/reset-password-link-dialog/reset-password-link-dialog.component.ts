import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { ToastService } from '../../../services/toast.service';
import { PasswordResetLinkResult } from '../../../auth/models/user.model';

@Component({
  selector: 'app-reset-password-link-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule],
  templateUrl: './reset-password-link-dialog.component.html',
  styleUrls: ['./reset-password-link-dialog.component.scss'],
})
export class ResetPasswordLinkDialogComponent {
  readonly dialogRef = inject(MatDialogRef<ResetPasswordLinkDialogComponent>);
  readonly data: PasswordResetLinkResult = inject(MAT_DIALOG_DATA);
  private readonly toast = inject(ToastService);

  copied = signal(false);

  /** Text shown / copied: prefer the absolute link, otherwise the raw token. */
  get shareValue(): string {
    return this.data.resetLink ?? this.data.resetToken ?? '';
  }

  copy(): void {
    const value = this.shareValue;
    if (!value) return;
    navigator.clipboard?.writeText(value).then(
      () => {
        this.copied.set(true);
        this.toast.success('Reset link copied to clipboard.');
        setTimeout(() => this.copied.set(false), 2000);
      },
      () => this.toast.error('Could not copy. Select and copy manually.'),
    );
  }

  close(): void {
    this.dialogRef.close();
  }
}
