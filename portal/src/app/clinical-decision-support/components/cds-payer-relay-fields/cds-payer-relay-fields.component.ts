import { Component, inject, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { CDS_COVERAGE_LIMITS } from '../../models/cds-coverage.models';

/** The relay controls of the payer form (owned by the payer dialog). */
export interface CdsPayerRelayControls {
  relayEnabled: FormControl<boolean>;
  relayServiceUrl: FormControl<string>;
  relayTimeoutMs: FormControl<number>;
  fallbackToLocalRules: FormControl<boolean>;
  relayClientId: FormControl<string>;
  forwardFhirAuthorization: FormControl<boolean>;
}

/**
 * The "Relay to the payer" part of the payer form: send this payer's calls on to the payer's own CRD service,
 * signed by Segue, instead of answering from local rules. Turning on "Send the EHR's access token" asks first.
 */
@Component({
  selector: 'app-cds-payer-relay-fields',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule],
  templateUrl: './cds-payer-relay-fields.component.html',
  styleUrl: './cds-payer-relay-fields.component.scss',
})
export class CdsPayerRelayFieldsComponent {
  private readonly dialog = inject(DialogService);

  readonly controls = input.required<CdsPayerRelayControls>();
  readonly showAllErrors = input(false);

  readonly limits = CDS_COVERAGE_LIMITS;

  hasError(name: 'relayServiceUrl' | 'relayTimeoutMs' | 'relayClientId', error?: string): boolean {
    const control = this.controls()[name];
    const visible = control.touched || this.showAllErrors();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  /** The checkbox was clicked: switching the token on needs a confirmation, switching it off does not. */
  onForwardTokenChange(event: Event): void {
    const box = event.target as HTMLInputElement;
    const control = this.controls().forwardFhirAuthorization;
    if (!box.checked) {
      control.setValue(false);
      control.markAsDirty();
      return;
    }
    // Hold it off until the admin confirms.
    control.setValue(false);
    box.checked = false;
    this.dialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '480px',
        data: {
          title: "Send the EHR's access token?",
          message:
            "The payer's service will receive the EHR's access token and FHIR address with every call, so it can read "
            + 'patient data from your EHR directly for as long as the token lasts. Turn this on only if your agreement '
            + 'with the payer covers it and the payer asked for it.',
          confirmLabel: 'Send the token',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        control.setValue(true);
        control.markAsDirty();
      });
  }
}
