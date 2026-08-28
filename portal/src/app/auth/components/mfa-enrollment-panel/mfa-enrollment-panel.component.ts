/**
 * MfaEnrollmentPanelComponent — the shared "scan QR / enter code / save backup codes" enrollment
 * flow, used both by the self-service Security settings page and by the forced first-login MFA
 * setup page (see mfa-setup-required.component.ts). Owns nothing about *why* enrollment is
 * happening — the host decides what "Cancel" means (or hides it entirely) and what happens after
 * `enrolled` fires.
 */
import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { toDataURL as qrToDataUrl } from 'qrcode';
import { MfaApiService } from '../../services/mfa-api.service';
import { MfaEnrollmentResponse } from '../../models/mfa.model';
import { extractApiErrorMessage } from '../../../core/http-error.util';
import { ToastService } from '../../../services/toast.service';

@Component({
  selector: 'app-mfa-enrollment-panel',
  standalone: true,
  imports: [],
  templateUrl: './mfa-enrollment-panel.component.html',
  styleUrl: './mfa-enrollment-panel.component.scss',
})
export class MfaEnrollmentPanelComponent implements OnInit {
  private readonly mfaApi   = inject(MfaApiService);
  private readonly toast    = inject(ToastService);

  /** Hide the Cancel button — for a forced first-login setup, there's nothing to cancel back to. */
  readonly allowCancel = input<boolean>(true);
  /** Start the enroll() call immediately rather than waiting for an explicit trigger. */
  readonly autoStart = input<boolean>(false);

  /** Fires once the user has confirmed a code and acknowledged their backup codes. */
  readonly enrolled = output<void>();
  /** Fires when the user cancels a staged (not-yet-confirmed) enrollment. */
  readonly cancelled = output<void>();

  protected readonly mfaBusy        = signal(false);
  protected readonly mfaError       = signal<string | null>(null);
  protected readonly mfaEnrollment  = signal<MfaEnrollmentResponse | null>(null);
  protected readonly mfaQrDataUrl   = signal<string | null>(null);
  protected readonly mfaBackupCodes = signal<string[] | null>(null);
  protected readonly copiedField    = signal<string | null>(null);

  // Signal inputs aren't bound yet inside the constructor (Angular sets them after construction,
  // before ngOnInit) — reading autoStart() there always sees its default (false). ngOnInit is the
  // first hook where the actual bound value is visible.
  ngOnInit(): void {
    if (this.autoStart()) {
      this.beginEnrollment();
    }
  }

  beginEnrollment(): void {
    this.mfaError.set(null);
    this.mfaBusy.set(true);
    this.mfaApi.enroll().subscribe({
      next: (res) => {
        this.mfaEnrollment.set(res);
        this.mfaBusy.set(false);
        this.mfaQrDataUrl.set(null);
        qrToDataUrl(res.otpAuthUri, { width: 150, margin: 1 })
          .then(url => this.mfaQrDataUrl.set(url))
          .catch(() => this.mfaQrDataUrl.set(null));
      },
      error: (err) => {
        this.mfaBusy.set(false);
        this.mfaError.set(extractApiErrorMessage(err, 'Could not start MFA enrollment. Please try again.'));
      },
    });
  }

  confirmEnrollment(code: string): void {
    if (!code.trim()) return;
    this.mfaBusy.set(true);
    this.mfaError.set(null);
    this.mfaApi.verify(code.trim()).subscribe({
      next: (res) => {
        this.mfaBackupCodes.set(res.backupCodes);
        this.mfaBusy.set(false);
      },
      error: (err) => {
        this.mfaBusy.set(false);
        this.mfaError.set(extractApiErrorMessage(err, 'Invalid code. Please try again.'));
      },
    });
  }

  cancelEnrollment(): void {
    this.mfaEnrollment.set(null);
    this.mfaQrDataUrl.set(null);
    this.mfaBackupCodes.set(null);
    this.mfaError.set(null);
    this.cancelled.emit();
  }

  finishEnrollment(): void {
    this.mfaEnrollment.set(null);
    this.mfaQrDataUrl.set(null);
    this.mfaBackupCodes.set(null);
    this.enrolled.emit();
  }

  copyToClipboard(value: string, field: string): void {
    navigator.clipboard.writeText(value).then(
      () => {
        this.copiedField.set(field);
        setTimeout(() => this.copiedField.set(null), 2000);
      },
      () => this.toast.error('Could not copy to clipboard.'),
    );
  }
}
