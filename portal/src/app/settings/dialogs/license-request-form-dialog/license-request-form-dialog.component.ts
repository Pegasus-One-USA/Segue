import { Component, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { LicenseService } from '../../services/license.service';
import { LicenseRequestStatus } from '../../models/license.model';

/** `{ mode: 'create' }` for a blank "Request a License" ask, or `{ mode: 'edit', request }` to correct
 *  an existing one's contact details (never its key) and re-submit it. */
export type LicenseRequestFormDialogData =
  | { mode: 'create' }
  | { mode: 'edit'; request: LicenseRequestStatus };

/** Submit-or-edit form for a single License Request — used both for a brand-new "New Request" and for
 *  correcting an existing one, since both collect the exact same fields. Calls the service itself and
 *  closes with the resulting `LicenseRequestStatus` on success (same pattern as every other
 *  DialogService-based create/edit dialog in this app), or `undefined` on Cancel/×. */
@Component({
  selector: 'app-license-request-form-dialog',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './license-request-form-dialog.component.html',
  styleUrl: './license-request-form-dialog.component.scss',
})
export class LicenseRequestFormDialogComponent {
  private readonly fb = inject(FormBuilder);
  private readonly licenseSvc = inject(LicenseService);
  readonly dialogRef = inject<DialogRef<LicenseRequestStatus | undefined>>(DialogRef);
  readonly data = inject<LicenseRequestFormDialogData>(DIALOG_DATA);

  protected readonly submitting = signal(false);
  protected readonly submitError = signal<string | null>(null);

  protected readonly isEdit = this.data.mode === 'edit';

  // Exposed as `form` (not e.g. `requestForm`) so DialogRef.attemptClose()'s generic duck-typed
  // unsaved-changes check (dialog.service.ts) picks it up with no extra override needed.
  readonly form = this.fb.nonNullable.group({
    clientName:  [this.data.mode === 'edit' ? this.data.request.clientName : '', Validators.required],
    email:       [this.data.mode === 'edit' ? this.data.request.email : '', [Validators.required, Validators.email]],
    companyName: [this.data.mode === 'edit' ? (this.data.request.companyName ?? '') : ''],
    address:     [this.data.mode === 'edit' ? (this.data.request.address ?? '') : ''],
    phoneNumber: [this.data.mode === 'edit' ? this.data.request.phoneNumber : '', Validators.required],
  });

  protected submit(): void {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.submitError.set(null);
    this.submitting.set(true);

    const raw = this.form.getRawValue();
    const body = {
      clientName:       raw.clientName.trim(),
      email:            raw.email.trim(),
      companyName:      raw.companyName.trim() || null,
      address:          raw.address.trim() || null,
      phoneNumber:      raw.phoneNumber.trim(),
      requestedFromUrl: window.location.origin,
    };

    const call = this.data.mode === 'edit'
      ? this.licenseSvc.updateRequest(this.data.request.id, body)
      : this.licenseSvc.createRequest(body);

    call.subscribe({
      next: (result) => {
        this.submitting.set(false);
        this.form.markAsPristine();
        this.dialogRef.close(result);
      },
      error: (err: HttpErrorResponse) => {
        this.submitting.set(false);
        this.submitError.set(
          err.error?.error_description ?? `Failed to ${this.isEdit ? 'update' : 'submit'} the license request.`,
        );
      },
    });
  }
}
