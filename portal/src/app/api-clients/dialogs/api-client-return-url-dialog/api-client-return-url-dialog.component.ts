import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IApiClientService } from '../../services/i-api-client.service';
import { ApiClient, ApiClientReturnUrl, ReturnUrlMatchMode } from '../../models/api-client.model';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface ApiClientReturnUrlDialogData {
  apiClientId: string;
  /** Present when editing an existing entry; absent when adding a new one. */
  existing?: ApiClientReturnUrl;
}

// Fast client-side feedback only — mirrors the server's own ValidateAndNormalizeReturnUrl /
// ValidateAndNormalizeReturnUrlDomain (ApiClientService.cs); the server re-validates regardless.
const EXACT_PATTERN = /^https?:\/\/[^\s?#]+$/i;
const DOMAIN_PATTERN = /^https?:\/\/[^\s/?#]+\/?$/i;

function patternFor(mode: ReturnUrlMatchMode): RegExp {
  return mode === 'Domain' ? DOMAIN_PATTERN : EXACT_PATTERN;
}

@Component({
  selector: 'app-api-client-return-url-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatRadioModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './api-client-return-url-dialog.component.html',
  styleUrls: ['./api-client-return-url-dialog.component.scss'],
})
export class ApiClientReturnUrlDialogComponent {
  private readonly svc = inject(IApiClientService);
  readonly dialogRef = inject<DialogRef<ApiClient>>(DialogRef);
  readonly data: ApiClientReturnUrlDialogData = inject(DIALOG_DATA) as ApiClientReturnUrlDialogData;

  readonly isEditing = !!this.data.existing;

  readonly mode = signal<ReturnUrlMatchMode>(this.data.existing?.matchMode ?? 'Exact');
  readonly url = signal(this.data.existing?.url ?? '');
  readonly label = signal(this.data.existing?.label ?? '');

  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly urlInvalid = computed(() => this.submitted() && !patternFor(this.mode()).test(this.url().trim()));

  readonly urlPlaceholder = computed(() =>
    this.mode() === 'Domain' ? 'https://app.example.com' : 'https://app.example.com/connect/callback');

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    const mode = this.mode();
    const url = this.url().trim();
    if (!patternFor(mode).test(url)) {
      return;
    }

    const label = this.label().trim() || null;
    this.saving.set(true);

    // No dedicated "update" endpoint exists (only Add/Remove) — editing removes the old row and adds the
    // new values, reaching the same end state through the same server-side validation every add goes
    // through anyway.
    const existing = this.data.existing;
    if (existing) {
      this.svc.removeReturnUrl(this.data.apiClientId, existing.id).subscribe({
        next: () => this.addNew(mode, url, label),
        error: (err: HttpErrorResponse) => {
          this.saving.set(false);
          this.errorMessage.set(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to update return URL.');
        },
      });
    } else {
      this.addNew(mode, url, label);
    }
  }

  private addNew(matchMode: ReturnUrlMatchMode, url: string, label: string | null): void {
    this.svc.addReturnUrl(this.data.apiClientId, { url, label, matchMode }).subscribe({
      next: client => {
        this.saving.set(false);
        this.dialogRef.close(client);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        const fallback = this.isEditing
          ? 'Removed the old return URL, but failed to add the new one. Please re-add it.'
          : 'Failed to add return URL.';
        this.errorMessage.set(err.error?.error ?? err.error?.title ?? err.error?.message ?? fallback);
      },
    });
  }
}
