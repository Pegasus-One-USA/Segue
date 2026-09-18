import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe, DecimalPipe } from '@angular/common';
import { LicenseService } from '../../services/license.service';
import {
  LicenseHistoryEntry, LicenseRequestStatus, LicenseStatus, LICENSE_UNLIMITED,
} from '../../models/license.model';
import { ToastService } from '../../../services/toast.service';
import { AppInitService } from '../../../onboarding/services/app-init.service';

/** Whether to show the full status card (Active/Grace/Expired) vs. the "No license activated"
 *  activation-only view. Deliberately NOT the same grouping as the backend's `IsPresent` (which is
 *  true for every state except Unlicensed, Invalid included) — an Invalid token never parsed into
 *  anything trustworthy to show a status card for, so it's grouped with Unlicensed here. */
function hasUsableLicense(status: LicenseStatus): boolean {
  return status.state === 'Active' || status.state === 'Grace' || status.state === 'Expired';
}

@Component({
  selector:    'app-license-settings',
  standalone:  true,
  imports:     [ReactiveFormsModule, DatePipe, DecimalPipe],
  templateUrl: './license-settings.component.html',
  styleUrl:    './license-settings.component.scss',
})
export class LicenseSettingsComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly licenseSvc = inject(LicenseService);
  private readonly toast = inject(ToastService);
  private readonly appInit = inject(AppInitService);

  /** Exposed for the template's `@if` checks against a numeric limit field — every such field uses this
   *  sentinel to mean "unlimited" rather than `null` (see `LicenseLimits` in license.model.ts). */
  protected readonly LICENSE_UNLIMITED = LICENSE_UNLIMITED;

  /** Renders a `LicenseLimits` numeric field for display: "Unlimited" for the sentinel, else the number. */
  protected formatLimit(value: number): string {
    return value === LICENSE_UNLIMITED ? 'Unlimited' : value.toLocaleString();
  }

  protected readonly loading = signal(true);
  protected readonly activating = signal(false);
  protected readonly status = signal<LicenseStatus | null>(null);

  /** True once a license has ever been applied (Active/Grace/Expired) — drives which top section
   *  (activation prompt vs. status card) renders. Derived from `status`, never set directly. */
  protected readonly hasLicense = computed(() => {
    const s = this.status();
    return s !== null && hasUsableLicense(s);
  });

  /** The "Update License" section starts collapsed once a license is already active/present, so the
   *  status card is what the admin sees first — matches the spec's "collapsible" requirement. Manually
   *  toggled after that (see toggleUpdateSection), so it stays a plain signal rather than a computed. */
  protected readonly showUpdateSection = signal(false);

  protected readonly applyError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    token: ['', Validators.required],
  });

  // ── License request (this install's own outbound request to the licensor) ─────────────────────
  protected readonly requestLoading = signal(true);
  protected readonly request = signal<LicenseRequestStatus | null>(null);
  protected readonly submittingRequest = signal(false);
  protected readonly requestError = signal<string | null>(null);

  protected readonly requestForm = this.fb.nonNullable.group({
    clientName:  ['', Validators.required],
    email:       ['', [Validators.required, Validators.email]],
    companyName: [''],
    address:     [''],
    phoneNumber: ['', Validators.required],
  });

  // ── License history ─────────────────────────────────────────────────────────────────────────
  protected readonly historyLoading = signal(true);
  protected readonly history = signal<LicenseHistoryEntry[]>([]);

  ngOnInit(): void {
    this.load();
    this.loadRequest();
    this.loadHistory();
  }

  private load(): void {
    this.loading.set(true);
    this.licenseSvc.get().subscribe({
      next: (s) => {
        this.status.set(s);
        this.showUpdateSection.set(!hasUsableLicense(s));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load license status');
      },
    });
  }

  private loadRequest(): void {
    this.requestLoading.set(true);
    this.licenseSvc.getRequest().subscribe({
      next: (r) => {
        this.request.set(r);
        this.requestLoading.set(false);
      },
      error: () => {
        this.requestLoading.set(false);
        this.toast.error('Failed to load license request status');
      },
    });
  }

  private loadHistory(): void {
    this.historyLoading.set(true);
    this.licenseSvc.getHistory().subscribe({
      next: (h) => {
        this.history.set(h);
        this.historyLoading.set(false);
      },
      error: () => {
        this.historyLoading.set(false);
        // Non-critical — the status card above already shows the current license; a failed history
        // fetch just leaves that section empty rather than blocking the rest of the page.
      },
    });
  }

  protected submitRequest(): void {
    if (this.requestForm.invalid) { this.requestForm.markAllAsTouched(); return; }
    this.requestError.set(null);
    this.submittingRequest.set(true);

    const raw = this.requestForm.getRawValue();
    this.licenseSvc.createRequest({
      clientName:  raw.clientName.trim(),
      email:       raw.email.trim(),
      companyName: raw.companyName.trim() || null,
      address:     raw.address.trim() || null,
      phoneNumber: raw.phoneNumber.trim(),
    }).subscribe({
      next: (r) => {
        this.submittingRequest.set(false);
        this.request.set(r);
        this.toast.success(
          r.status === 'Submitted' ? 'License request sent' : 'License request saved',
          r.status === 'Submitted'
            ? "We'll be in touch once it's ready."
            : 'Could not reach the licensor directly — share the code below with them instead.'
        );
      },
      error: (err: HttpErrorResponse) => {
        this.submittingRequest.set(false);
        this.requestError.set(err.error?.error_description ?? 'Failed to submit the license request.');
      },
    });
  }

  protected resendRequest(): void {
    this.submittingRequest.set(true);
    this.licenseSvc.resubmitRequest().subscribe({
      next: (r) => {
        this.submittingRequest.set(false);
        this.request.set(r);
        this.toast.success(
          r.status === 'Submitted' ? 'License request re-sent' : 'License request saved',
          r.status === 'Submitted'
            ? "We'll be in touch once it's ready."
            : 'Could not reach the licensor directly — share the code below with them instead.'
        );
      },
      error: (err: HttpErrorResponse) => {
        this.submittingRequest.set(false);
        this.toast.error(err.error?.error_description ?? 'Failed to resend the license request.');
      },
    });
  }

  protected copyEncodedPayload(): void {
    const payload = this.request()?.encodedPayload;
    if (!payload) return;
    navigator.clipboard?.writeText(payload).then(
      () => this.toast.success('Copied to clipboard'),
      () => this.toast.error('Could not copy. Select and copy manually.'),
    );
  }

  protected toggleUpdateSection(): void {
    this.showUpdateSection.update((v) => !v);
  }

  protected activate(): void {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.applyError.set(null);
    this.activating.set(true);

    const token = this.form.getRawValue().token.trim();
    this.licenseSvc.apply({ token }).subscribe({
      next: (s) => {
        this.activating.set(false);
        this.status.set(s);
        this.showUpdateSection.set(!hasUsableLicense(s));
        this.form.reset({ token: '' });
        this.toast.success('License activated');
        this.appInit.refreshLicenseGate();
      },
      error: (err: HttpErrorResponse) => {
        this.activating.set(false);
        this.applyError.set(
          err.error?.error_description ?? err.error?.title ?? 'The license token failed verification.'
        );
      },
    });
  }
}
