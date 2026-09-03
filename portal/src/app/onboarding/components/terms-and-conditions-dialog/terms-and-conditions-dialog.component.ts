import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { A11yModule } from '@angular/cdk/a11y';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { LEGAL_ENDPOINTS } from '../../../core/api-endpoints';

/** Result returned via dialogRef.close() — callers gate acceptance on readToEnd, never on the dialog
 *  merely having been opened. */
export interface TermsAndConditionsDialogResult {
  readToEnd: boolean;
}

/**
 * Renders the Terms & Conditions static HTML file (Content/legal/terms-and-conditions.html, served by
 * the API at LEGAL_ENDPOINTS.termsAndConditions) inside a dialog. Fetched as text and bound via
 * [innerHTML] with DomSanitizer rather than an <iframe> — this API sets X-Frame-Options: DENY and a
 * frame-ancestors 'none' CSP on every response, which would block framing the same content.
 *
 * Tracks whether the reader has scrolled the content to its end and reports that back via the closed
 * result, so callers (setup-super-admin, signup) can gate their "accept" checkbox on having actually
 * scrolled through the document rather than just having opened the dialog.
 */
@Component({
  selector: 'app-terms-and-conditions-dialog',
  standalone: true,
  imports: [CommonModule, A11yModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    <h2 mat-dialog-title>Terms and Conditions</h2>
    <mat-dialog-content #scrollBody class="tcd-content" cdkFocusInitial (scroll)="checkScrollPosition()">
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
      <span class="tcd-scroll-hint" [class.tcd-scroll-hint--done]="readToEnd()">
        @if (readToEnd()) {
          <mat-icon inline="true">check_circle</mat-icon> You've read the full document.
        } @else if (!loading() && !error()) {
          Scroll to the end to enable acceptance.
        }
      </span>
      <button type="button" mat-flat-button color="primary" (click)="close()">Close</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .tcd-content { min-width: 480px; max-width: 640px; max-height: 60vh; }
    .tcd-loading { display: flex; align-items: center; gap: 12px; padding: 24px 0; color: var(--text-muted, #666); }
    .tcd-error { display: flex; align-items: center; gap: 8px; color: #b3261e; }
    .tcd-html { font-size: 14px; }
    .tcd-scroll-hint { margin-right: auto; font-size: 13px; color: var(--text-muted, #666); display: flex; align-items: center; gap: 4px; }
    .tcd-scroll-hint--done { color: #1e7e34; }
  `],
})
export class TermsAndConditionsDialogComponent {
  private readonly dialogRef = inject(MatDialogRef<TermsAndConditionsDialogComponent, TermsAndConditionsDialogResult>);
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);

  private readonly scrollBody = viewChild<ElementRef<HTMLElement>>('scrollBody');

  protected readonly loading = signal(true);
  protected readonly error = signal(false);
  protected readonly html = signal<SafeHtml>('');
  protected readonly readToEnd = signal(false);

  constructor() {
    this.http.get(LEGAL_ENDPOINTS.termsAndConditions, { responseType: 'text' }).subscribe({
      next: (raw) => {
        this.html.set(this.sanitizer.bypassSecurityTrustHtml(raw));
        this.loading.set(false);
        // The content might already fit without any scrolling (short document, tall viewport) — check
        // once the DOM has painted the newly-bound HTML AND the dialog's own open animation has
        // settled. Measuring too early (e.g. a bare setTimeout(fn, 0)) can read clientHeight/scrollHeight
        // before mat-dialog-content's max-height has actually clamped the box, making unscrolled content
        // look already-at-the-bottom.
        setTimeout(() => this.checkScrollPosition(), 300);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected checkScrollPosition(): void {
    const el = this.scrollBody()?.nativeElement;
    if (!el || this.readToEnd()) {
      return;
    }
    // 16px tolerance — fractional scroll positions (browser zoom, subpixel layout) can land just short
    // of the exact scrollHeight even when the reader has genuinely reached the bottom.
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 16) {
      this.readToEnd.set(true);
    }
  }

  close(): void {
    this.dialogRef.close({ readToEnd: this.readToEnd() });
  }
}
