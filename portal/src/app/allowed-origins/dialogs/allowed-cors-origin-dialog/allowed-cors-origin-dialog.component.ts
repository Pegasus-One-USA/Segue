import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IAllowedCorsOriginService } from '../../services/i-allowed-cors-origin.service';
import { AllowedCorsOrigin, CreateAllowedCorsOriginRequest, UpdateAllowedCorsOriginRequest } from '../../models/allowed-cors-origin.model';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

// Origin only, mirrors the backend's ValidateAndNormalize (AllowedCorsOriginsService.cs): absolute
// http(s) URL, no path/query/fragment. The server re-validates regardless — this is fast feedback only.
const ORIGIN_PATTERN = /^https?:\/\/[^\s/]+$/i;

export interface AllowedCorsOriginDialogData {
  origin?: AllowedCorsOrigin;
}

@Component({
  selector: 'app-allowed-cors-origin-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './allowed-cors-origin-dialog.component.html',
  styleUrls: ['./allowed-cors-origin-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class AllowedCorsOriginDialogComponent {
  private readonly svc = inject(IAllowedCorsOriginService);
  private readonly fb  = inject(FormBuilder);
  readonly dialogRef   = inject<DialogRef<boolean>>(DialogRef);
  readonly data: AllowedCorsOriginDialogData = inject(DIALOG_DATA) as AllowedCorsOriginDialogData;

  readonly isEdit       = !!this.data?.origin;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    originUrl: [
      this.data?.origin?.originUrl ?? '',
      [Validators.required, Validators.pattern(ORIGIN_PATTERN), Validators.maxLength(500)],
    ],
    label: [
      this.data?.origin?.label ?? '',
      [Validators.maxLength(200)],
    ],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const raw = this.form.getRawValue();
    const request: CreateAllowedCorsOriginRequest | UpdateAllowedCorsOriginRequest = {
      originUrl: raw.originUrl!.trim(),
      label: raw.label?.trim() || null,
    };
    this.saving.set(true);

    const request$ = this.isEdit
      ? this.svc.update(this.data.origin!.id, request)
      : this.svc.create(request);

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save origin. Please try again.');
      },
    });
  }
}
