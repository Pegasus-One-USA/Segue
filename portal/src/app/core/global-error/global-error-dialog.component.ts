import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/** Data passed into the Phase 6A friendly-error dialog — safe, user-facing values only. */
export interface GlobalErrorDialogData {
  message: string;
  referenceId: string | null;
  category: string | null;
}

/**
 * Phase 6A – the ONE dialog shown to end users when the backend returns a standardized error response.
 * Shows the user-friendly message and the quotable Error Reference ID. Deliberately shows NO technical
 * detail (no stack trace, exception type, or raw message).
 */
@Component({
  selector: 'app-global-error-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <div class="ge-dialog">
      <div class="ge-header">
        <mat-icon aria-hidden="true">error_outline</mat-icon>
        <h2 mat-dialog-title>Something went wrong</h2>
      </div>

      <mat-dialog-content>
        <p class="ge-message">{{ data.message }}</p>

        @if (data.referenceId) {
          <div class="ge-reference">
            <span class="ge-reference-label">Reference ID</span>
            <div class="ge-reference-value">
              <code>{{ data.referenceId }}</code>
              <button
                type="button"
                class="ge-copy"
                (click)="copy()"
                [attr.aria-label]="'Copy reference ID ' + data.referenceId"
              >
                <mat-icon aria-hidden="true">{{ copied() ? 'check' : 'content_copy' }}</mat-icon>
              </button>
            </div>
          </div>
          <p class="ge-hint">Please contact your system administrator and provide this reference ID.</p>
        }
      </mat-dialog-content>

      <mat-dialog-actions align="end">
        <button mat-flat-button color="primary" (click)="dialogRef.close()">Dismiss</button>
      </mat-dialog-actions>
    </div>
  `,
  styles: [`
    .ge-dialog { min-width: 22rem; max-width: 30rem; }
    .ge-header { display: flex; align-items: center; gap: 0.5rem; }
    .ge-header mat-icon { color: var(--color-error); }
    .ge-header h2 { margin: 0; }
    .ge-message { margin: 0.5rem 0 1rem; line-height: 1.5; }
    .ge-reference {
      background: rgba(0, 0, 0, 0.04);
      border-radius: 8px;
      padding: 0.75rem 1rem;
      margin-bottom: 0.75rem;
    }
    .ge-reference-label {
      display: block;
      font-size: 0.75rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      opacity: 0.7;
      margin-bottom: 0.25rem;
    }
    .ge-reference-value { display: flex; align-items: center; gap: 0.5rem; }
    .ge-reference-value code { font-size: 1rem; font-weight: 600; }
    .ge-copy {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border: none;
      background: transparent;
      cursor: pointer;
      padding: 0.15rem;
      color: inherit;
      opacity: 0.7;
    }
    .ge-copy:hover { opacity: 1; }
    .ge-copy mat-icon { font-size: 1.1rem; width: 1.1rem; height: 1.1rem; }
    .ge-hint { font-size: 0.85rem; opacity: 0.75; margin: 0; }
  `],
})
export class GlobalErrorDialogComponent {
  readonly dialogRef = inject(MatDialogRef<GlobalErrorDialogComponent>);
  readonly data = inject<GlobalErrorDialogData>(MAT_DIALOG_DATA);

  readonly copied = signal(false);

  copy(): void {
    if (!this.data.referenceId) { return; }
    navigator.clipboard?.writeText(this.data.referenceId).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    });
  }
}
