import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CDS_COVERAGE_LIMITS, CdsPayer, SaveCdsPayerRequest } from '../../models/cds-coverage.models';
import { absoluteHttpsUrlValidator } from '../../models/cds-validators';
import { CdsChipListInputComponent } from '../../components/cds-chip-list-input/cds-chip-list-input.component';
import { CdsPayerRelayFieldsComponent } from '../../components/cds-payer-relay-fields/cds-payer-relay-fields.component';

export interface CdsPayerDialogData {
  /** Set when editing. */
  payer?: CdsPayer;
}

const L = CDS_COVERAGE_LIMITS;

/** A relay address must not carry a query string (the API refuses one). Error key: `query`. */
function noQueryValidator(control: AbstractControl): ValidationErrors | null {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  return value.includes('?') ? { query: true } : null;
}

/** At least one identifier. Error key: `required`. */
function notEmptyList(control: AbstractControl): ValidationErrors | null {
  return Array.isArray(control.value) && control.value.length > 0 ? null : { required: true };
}

/** The relay address is needed once the relay is on (reads the sibling switch). Error key: `required`. */
function requiredWhenRelayOn(control: AbstractControl): ValidationErrors | null {
  const on = control.parent?.get('relayEnabled')?.value === true;
  const url = typeof control.value === 'string' ? control.value.trim() : '';
  return on && !url ? { required: true } : null;
}

/**
 * Add or edit a coverage requirements payer: how Segue recognises the payer on the patient's coverage, and whether
 * its calls are answered from local rules or relayed to the payer's own CRD service.
 */
@Component({
  selector: 'app-cds-payer-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, CdsChipListInputComponent, CdsPayerRelayFieldsComponent],
  templateUrl: './cds-payer-dialog.component.html',
  styleUrl: './cds-payer-dialog.component.scss',
})
// DialogRef.attemptClose() reads `form.dirty` itself, so leaving with edits asks first.
export class CdsPayerDialogComponent {
  private readonly api = inject(CdsCoverageApiService);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly dialogRef = inject<DialogRef<CdsPayer>>(DialogRef);
  readonly data = (inject(DIALOG_DATA) as CdsPayerDialogData | null) ?? {};

  readonly isEdit = !!this.data.payer;
  readonly limits = L;
  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  private readonly payer = this.data.payer;

  readonly form = new FormGroup(
    {
      name: new FormControl(this.payer?.name ?? '', { nonNullable: true, validators: [Validators.required, Validators.maxLength(L.payerNameMax)] }),
      identifiers: new FormControl<string[]>([...(this.payer?.identifiers ?? [])], { nonNullable: true, validators: [notEmptyList] }),
      planValues: new FormControl<string[]>([...(this.payer?.planValues ?? [])], { nonNullable: true }),
      isEnabled: new FormControl(this.payer?.isEnabled ?? true, { nonNullable: true }),
      relayEnabled: new FormControl(this.payer?.relayEnabled ?? false, { nonNullable: true }),
      relayServiceUrl: new FormControl(this.payer?.relayServiceUrl ?? '', {
        nonNullable: true, validators: [requiredWhenRelayOn, Validators.maxLength(L.urlMax), absoluteHttpsUrlValidator, noQueryValidator],
      }),
      relayTimeoutMs: new FormControl(this.payer?.relayTimeoutMs ?? L.relayTimeoutDefault, {
        nonNullable: true,
        validators: [Validators.required, Validators.min(L.relayTimeoutMin), Validators.max(L.relayTimeoutMax), Validators.pattern(/^\d+$/)],
      }),
      fallbackToLocalRules: new FormControl(this.payer?.fallbackToLocalRules ?? true, { nonNullable: true }),
      relayClientId: new FormControl(this.payer?.relayClientId ?? '', { nonNullable: true, validators: [Validators.maxLength(L.clientIdMax)] }),
      forwardFhirAuthorization: new FormControl(this.payer?.forwardFhirAuthorization ?? false, { nonNullable: true }),
    },
  );

  readonly relayControls = {
    relayEnabled: this.form.controls.relayEnabled,
    relayServiceUrl: this.form.controls.relayServiceUrl,
    relayTimeoutMs: this.form.controls.relayTimeoutMs,
    fallbackToLocalRules: this.form.controls.fallbackToLocalRules,
    relayClientId: this.form.controls.relayClientId,
    forwardFhirAuthorization: this.form.controls.forwardFhirAuthorization,
  };

  constructor() {
    // The address is required only while the relay is on, so re-check it when the switch changes.
    const url = this.form.controls.relayServiceUrl;
    url.updateValueAndValidity({ emitEvent: false });
    this.form.controls.relayEnabled.valueChanges.subscribe(on => {
      url.updateValueAndValidity();
      if (!on) this.clearHiddenRelayErrors();
    });
  }

  /**
   * The relay fields are hidden while the relay is off, so an error in them could not be seen or fixed and would
   * block Save. Turning the relay off therefore clears a bad address and puts a bad wait time back to the default
   * (valid values are kept, so turning the relay back on restores them).
   */
  private clearHiddenRelayErrors(): void {
    const { relayServiceUrl, relayTimeoutMs, relayClientId } = this.form.controls;
    if (relayServiceUrl.invalid) relayServiceUrl.setValue('');
    if (relayTimeoutMs.invalid) relayTimeoutMs.setValue(L.relayTimeoutDefault);
    if (relayClientId.invalid) relayClientId.setValue('');
  }

  hasError(name: 'name' | 'identifiers', error?: string): boolean {
    const control = this.form.controls[name];
    const visible = control.touched || this.submitted();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  cancel(): void {
    this.dialogRef.attemptClose();
  }

  save(): void {
    const code = this.isEdit ? 'cdshooks.edit' : 'cdshooks.create';
    if (!this.actionGuard.ensure(code, `You do not have permission to ${this.isEdit ? 'edit' : 'add'} payers.`)) return;

    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    const request: SaveCdsPayerRequest = {
      name: v.name.trim(),
      identifiers: v.identifiers,
      planValues: v.planValues,
      isEnabled: v.isEnabled,
      relayEnabled: v.relayEnabled,
      relayServiceUrl: v.relayServiceUrl.trim() || null,
      relayTimeoutMs: Number(v.relayTimeoutMs),
      fallbackToLocalRules: v.fallbackToLocalRules,
      // Never send the token for a payer whose calls are not relayed.
      forwardFhirAuthorization: v.relayEnabled && v.forwardFhirAuthorization,
      relayClientId: v.relayClientId.trim() || null,
    };

    this.saving.set(true);
    const request$ = this.payer ? this.api.updatePayer(this.payer.id, request) : this.api.createPayer(request);
    request$.subscribe({
      next: saved => {
        this.saving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.message || 'Could not save the payer.');
      },
    });
  }
}
