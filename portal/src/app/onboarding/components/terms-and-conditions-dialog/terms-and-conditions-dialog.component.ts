import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { A11yModule } from '@angular/cdk/a11y';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { LEGAL_ENDPOINTS } from '../../../core/api-endpoints';

/** Result returned via dialogRef.close() — callers gate acceptance on readToEnd, which is true once
 *  the reader has closed the dialog (there's no longer a scroll-to-bottom requirement). A dialog
 *  dismissed via backdrop click/Escape resolves with no result at all, which callers already treat
 *  as "not accepted". */
export interface TermsAndConditionsDialogResult {
  readToEnd: boolean;
}

/**
 * Renders the Terms & Conditions static HTML file (Content/legal/terms-and-conditions.html, served by
 * the API at LEGAL_ENDPOINTS.termsAndConditions) inside a dialog. Fetched as text and bound via
 * [innerHTML] with DomSanitizer rather than an <iframe> — this API sets X-Frame-Options: DENY and a
 * frame-ancestors 'none' CSP on every response, which would block framing the same content.
 *
 * Acceptance is gated on having opened and closed this dialog (see callers: setup-super-admin,
 * set-password, signup) — not on scrolling the content to its physical end.
 */
@Component({
  selector: 'app-terms-and-conditions-dialog',
  standalone: true,
  imports: [CommonModule, A11yModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    <h2 mat-dialog-title>Terms and Conditions</h2>
    <mat-dialog-content class="tcd-content" cdkFocusInitial>
      @if (loading()) {
        <div class="tcd-loading">
          <mat-spinner diameter="28" strokeWidth="2"></mat-spinner>
          <span>Loading…</span>
        </div>
      } @else if (error()) {
        <p class="tcd-error">
          <mat-icon>error_outline</mat-icon>
          Unable to load the Terms and Conditions right now. Please try again.
        </p>
      } @else {
        <div class="tcd-html" [innerHTML]="html()"></div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button type="button" mat-flat-button color="primary" (click)="close()">Close</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .tcd-content { min-width: 480px; max-width: 640px; max-height: 60vh; }
    .tcd-loading { display: flex; align-items: center; gap: 12px; padding: 24px 0; color: var(--text-muted, #666); }
    .tcd-error { display: flex; align-items: center; gap: 8px; color: #b3261e; }
    .tcd-html { font-size: 14px; }
  `],
})
export class TermsAndConditionsDialogComponent {
  private readonly dialogRef = inject(MatDialogRef<TermsAndConditionsDialogComponent, TermsAndConditionsDialogResult>);
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);

  protected readonly loading = signal(true);
  protected readonly error = signal(false);
  protected readonly html = signal<SafeHtml>('');

  constructor() {
    this.http.get(LEGAL_ENDPOINTS.termsAndConditions, { responseType: 'text' }).subscribe({
      next: (raw) => {
        this.html.set(this.sanitizer.bypassSecurityTrustHtml(raw));
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  close(): void {
    this.dialogRef.close({ readToEnd: true });
  }
}
