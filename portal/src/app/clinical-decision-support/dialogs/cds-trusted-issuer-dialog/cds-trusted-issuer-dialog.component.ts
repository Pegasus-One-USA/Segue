import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsTrustedIssuer, SaveCdsTrustedIssuerRequest } from '../../models/cds.models';
import { CDS_SANDBOX, CDS_VENDOR_SUGGESTIONS } from '../../models/cds-sandbox';
import { absoluteHttpsUrlValidator, fhirBaseUrlValidator, jwksJsonValidator } from '../../models/cds-validators';
import { CdsIssuerEpicFieldsComponent } from '../../components/cds-issuer-epic-fields/cds-issuer-epic-fields.component';

export interface CdsTrustedIssuerDialogData {
  /** Set when editing. */
  issuer?: CdsTrustedIssuer;
  /** Starts a new issuer with the public sandbox's values. */
  sandbox?: boolean;
}

/** At least one way to get the issuer's public keys. Error key on the group: `keysRequired`. */
function keysRequired(group: AbstractControl): ValidationErrors | null {
  const url = (group.get('jwksUrl')?.value as string | null)?.trim();
  const json = (group.get('jwksJson')?.value as string | null)?.trim();
  return url || json ? null : { keysRequired: true };
}

/** Add or edit a trusted issuer: whose signed calls are accepted and where their public keys are. */
@Component({
  selector: 'app-cds-trusted-issuer-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, CdsIssuerEpicFieldsComponent],
  templateUrl: './cds-trusted-issuer-dialog.component.html',
  styleUrl: './cds-trusted-issuer-dialog.component.scss',
})
// DialogRef.attemptClose() reads `form.dirty` itself, so leaving with edits asks first.
export class CdsTrustedIssuerDialogComponent {
  private readonly api = inject(CdsApiService);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly dialogRef = inject<DialogRef<CdsTrustedIssuer>>(DialogRef);
  readonly data = (inject(DIALOG_DATA) as CdsTrustedIssuerDialogData | null) ?? {};

  readonly isEdit = !!this.data.issuer;
  readonly vendorSuggestions = CDS_VENDOR_SUGGESTIONS;
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = new FormGroup(
    {
      name: new FormControl(this.data.issuer?.name ?? (this.data.sandbox ? CDS_SANDBOX.name : ''), {
        nonNullable: true, validators: [Validators.required, Validators.maxLength(200)],
      }),
      vendor: new FormControl(this.data.issuer?.vendor ?? (this.data.sandbox ? CDS_SANDBOX.vendor : ''), {
        nonNullable: true, validators: [Validators.required, Validators.maxLength(100)],
      }),
      issuer: new FormControl(this.data.issuer?.issuer ?? (this.data.sandbox ? CDS_SANDBOX.issuer : ''), {
        nonNullable: true, validators: [Validators.required, Validators.maxLength(500)],
      }),
      jwksUrl: new FormControl(this.data.issuer?.jwksUrl ?? (this.data.sandbox ? CDS_SANDBOX.jwksUrl : ''), {
        nonNullable: true, validators: [Validators.maxLength(1000), absoluteHttpsUrlValidator],
      }),
      jwksJson: new FormControl(this.data.issuer?.jwksJson ?? '', { nonNullable: true, validators: [jwksJsonValidator] }),
      audience: new FormControl(this.data.issuer?.audience ?? '', { nonNullable: true, validators: [Validators.maxLength(500)] }),
      isEnabled: new FormControl(this.data.issuer?.isEnabled ?? true, { nonNullable: true }),
      expectedSubject: new FormControl(this.data.issuer?.expectedSubject ?? '', {
        nonNullable: true, validators: [Validators.maxLength(500)],
      }),
      fhirBaseUrl: new FormControl(this.data.issuer?.fhirBaseUrl ?? '', {
        nonNullable: true, validators: [Validators.maxLength(1000), fhirBaseUrlValidator],
      }),
      allowCallback: new FormControl(this.data.issuer?.allowCallback ?? true, { nonNullable: true }),
    },
    { validators: [keysRequired] },
  );

  hasError(name: 'name' | 'vendor' | 'issuer' | 'jwksUrl' | 'jwksJson' | 'audience', error?: string): boolean {
    const control = this.form.controls[name];
    const visible = control.touched || this.submitted();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  keysMissing(): boolean {
    return this.form.hasError('keysRequired') && this.submitted();
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  cancel(): void {
    this.dialogRef.attemptClose();
  }

  save(): void {
    const code = this.isEdit ? 'cdshooks.edit' : 'cdshooks.create';
    if (!this.actionGuard.ensure(code, `You do not have permission to ${this.isEdit ? 'edit' : 'add'} trusted issuers.`)) return;

    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    const request: SaveCdsTrustedIssuerRequest = {
      name: v.name.trim(),
      vendor: v.vendor.trim(),
      issuer: v.issuer.trim(),
      jwksUrl: v.jwksUrl.trim() || null,
      jwksJson: v.jwksJson.trim() || null,
      audience: v.audience.trim() || null,
      isEnabled: v.isEnabled,
      expectedSubject: v.expectedSubject.trim() || null,
      fhirBaseUrl: v.fhirBaseUrl.trim().replace(/\/+$/, '') || null,
      allowCallback: v.allowCallback,
    };

    this.saving.set(true);
    const request$ = this.data.issuer
      ? this.api.updateTrustedIssuer(this.data.issuer.id, request)
      : this.api.createTrustedIssuer(request);

    request$.subscribe({
      next: saved => {
        this.saving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.message || 'Could not save the trusted issuer.');
      },
    });
  }
}
