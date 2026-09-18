import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe, DecimalPipe } from '@angular/common';
import { LicenseService } from '../../services/license.service';
import { LicenseStatus, LICENSE_UNLIMITED } from '../../models/license.model';
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

  ngOnInit(): void {
    this.load();
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
