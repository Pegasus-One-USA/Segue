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
import { DialogService } from '../../../core/services/dialog.service';
import { LicenseHistoryDetailDialogComponent } from '../../dialogs/license-history-detail-dialog/license-history-detail-dialog.component';
import {
  LicenseRequestFormDialogComponent, LicenseRequestFormDialogData,
} from '../../dialogs/license-request-form-dialog/license-request-form-dialog.component';

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
  private readonly dialog = inject(DialogService);

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

  /** Which of the four tabs is showing — a plain signal, not derived, since it's purely a user-driven
   *  view choice with no other state it needs to stay in sync with. */
  protected readonly activeTab = signal<'status' | 'update' | 'request' | 'history'>('status');

  /** True once a license has ever been applied (Active/Grace/Expired) — drives which top section
   *  (activation prompt vs. status card) renders. Derived from `status`, never set directly. */
  protected readonly hasLicense = computed(() => {
    const s = this.status();
    return s !== null && hasUsableLicense(s);
  });

  protected readonly applyError = signal<string | null>(null);
  protected readonly clearingLicense = signal(false);
  protected readonly clearingHistory = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    token: ['', Validators.required],
  });

  // ── License requests (this install's own outbound requests to the licensor — any number of them,
  // each independent; "Submit Request" always creates a new one, never blocked by an existing one) ──
  protected readonly requestLoading = signal(true);
  protected readonly requests = signal<LicenseRequestStatus[]>([]);

  // Which request (by id) is currently being re-sent as a new request, so only that row's button shows
  // "Sending…" instead of every row in the list at once.
  protected readonly resubmittingRequestId = signal<string | null>(null);

  // Which request (by id) is currently being soft-deleted — same per-row-loading pattern as resend.
  protected readonly deletingRequestId = signal<string | null>(null);

  // Base URL this install posts license requests to — a runtime SystemSetting (License:
  // LicensorApplicationUrl), editable here since it's the one detail a self-hosted deployment may need
  // to change (e.g. the licensor moving to a new domain). Read/written via LicenseService's own narrow
  // licensor-url endpoint (not the general ISystemSettingsService), since that's the only system
  // setting the license gate allowlists while unlicensed — see LicenseRequestController.
  protected readonly licensorUrlLoading = signal(true);
  protected readonly licensorUrlSaving = signal(false);
  // A bare <form [formGroup]> (rather than a lone FormControl bound with [formControl]) is what makes
  // Angular's FormGroupDirective intercept the native submit event and call preventDefault() — without
  // it, (ngSubmit) still fires, but the browser also does its own full-page GET/POST navigation right
  // alongside it.
  protected readonly licensorUrlForm = this.fb.nonNullable.group({
    url: ['', Validators.required],
  });

  // ── License history ─────────────────────────────────────────────────────────────────────────
  protected readonly historyLoading = signal(true);
  protected readonly history = signal<LicenseHistoryEntry[]>([]);

  ngOnInit(): void {
    this.load();
    this.loadRequests();
    this.loadLicensorUrl();
    this.loadHistory();
  }

  private loadLicensorUrl(): void {
    this.licensorUrlLoading.set(true);
    this.licenseSvc.getLicensorUrl().subscribe({
      next: (setting) => {
        this.licensorUrlForm.setValue({ url: setting.url });
        this.licensorUrlForm.markAsPristine();
        this.licensorUrlLoading.set(false);
      },
      error: () => {
        this.licensorUrlLoading.set(false);
        // Non-critical — a SuperAdmin who can't load this can still submit a request against whatever
        // the server already has configured; only editing it here is blocked.
      },
    });
  }

  protected saveLicensorUrl(): void {
    if (this.licensorUrlForm.invalid) { this.licensorUrlForm.markAllAsTouched(); return; }
    this.licensorUrlSaving.set(true);

    this.licenseSvc.setLicensorUrl(this.licensorUrlForm.getRawValue().url.trim()).subscribe({
      next: (setting) => {
        this.licensorUrlSaving.set(false);
        this.licensorUrlForm.setValue({ url: setting.url });
        this.licensorUrlForm.markAsPristine();
        this.toast.success('Licensor application URL saved');
      },
      error: (err: HttpErrorResponse) => {
        this.licensorUrlSaving.set(false);
        this.toast.error(err.error?.error_description ?? 'Failed to save the licensor application URL.');
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.licenseSvc.get().subscribe({
      next: (s) => {
        this.status.set(s);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load license status');
      },
    });
  }

  private loadRequests(): void {
    this.requestLoading.set(true);
    this.licenseSvc.getRequests().subscribe({
      next: (r) => {
        this.requests.set(r);
        this.requestLoading.set(false);
      },
      error: () => {
        this.requestLoading.set(false);
        this.toast.error('Failed to load license requests');
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

  /** Replaces one request in the list with its freshly-returned state, or prepends a brand-new one —
   *  avoids a full reload after every submit/resend/edit. */
  private upsertRequestInList(r: LicenseRequestStatus): void {
    const current = this.requests();
    const index = current.findIndex((x) => x.id === r.id);
    this.requests.set(
      index === -1 ? [r, ...current] : current.map((x, i) => (i === index ? r : x)),
    );
  }

  protected openNewRequestDialog(): void {
    this.dialog.open<LicenseRequestFormDialogComponent, LicenseRequestFormDialogData, LicenseRequestStatus | undefined>(
      LicenseRequestFormDialogComponent, { data: { mode: 'create' }, disableClose: true },
    ).afterClosed().subscribe((result) => {
        if (!result) return;
        this.upsertRequestInList(result);
        this.toast.success(
          result.status === 'Submitted' ? 'License request sent' : 'License request saved',
          result.status === 'Submitted'
            ? "We'll be in touch once it's ready."
            : 'Could not reach the licensor directly — share the code from that row with them instead.'
        );
      });
  }

  /** "Resend" submits a brand-new request carrying this row's same details — every request is
   *  independent now, so re-sending shows up as its own new entry (its own timestamp and key) rather
   *  than mutating the original. */
  protected resendRequest(r: LicenseRequestStatus): void {
    this.resubmittingRequestId.set(r.id);
    this.licenseSvc.createRequest({
      clientName:       r.clientName,
      email:            r.email,
      companyName:      r.companyName,
      address:          r.address,
      phoneNumber:      r.phoneNumber,
      requestedFromUrl: window.location.origin,
    }).subscribe({
      next: (created) => {
        this.resubmittingRequestId.set(null);
        this.upsertRequestInList(created);
        this.toast.success(
          created.status === 'Submitted' ? 'License request re-sent' : 'License request saved',
          created.status === 'Submitted'
            ? "We'll be in touch once it's ready."
            : 'Could not reach the licensor directly — share the code below with them instead.'
        );
      },
      error: (err: HttpErrorResponse) => {
        this.resubmittingRequestId.set(null);
        this.toast.error(err.error?.error_description ?? 'Failed to resend the license request.');
      },
    });
  }

  protected openEditRequestDialog(r: LicenseRequestStatus): void {
    this.dialog.open<LicenseRequestFormDialogComponent, LicenseRequestFormDialogData, LicenseRequestStatus | undefined>(
      LicenseRequestFormDialogComponent, { data: { mode: 'edit', request: r }, disableClose: true },
    ).afterClosed().subscribe((result) => {
        if (!result) return;
        this.upsertRequestInList(result);
        this.toast.success('License request updated');
      });
  }

  /** Soft-deletes a request — hides it from this list; the licensor's own copy and its key are
   *  untouched (a license already minted against it keeps working). */
  protected deleteRequest(r: LicenseRequestStatus): void {
    if (!confirm(`Delete the request for "${r.clientName}"? It will disappear from this list; support can restore it later if needed.`)) {
      return;
    }

    this.deletingRequestId.set(r.id);
    this.licenseSvc.deleteRequest(r.id).subscribe({
      next: () => {
        this.deletingRequestId.set(null);
        this.requests.set(this.requests().filter((x) => x.id !== r.id));
        this.toast.success('License request deleted');
      },
      error: (err: HttpErrorResponse) => {
        this.deletingRequestId.set(null);
        this.toast.error(err.error?.error_description ?? 'Failed to delete the license request.');
      },
    });
  }

  protected openHistoryDetail(entry: LicenseHistoryEntry): void {
    this.dialog.open(LicenseHistoryDetailDialogComponent, { data: entry, width: '640px' });
  }

  protected copyEncodedPayload(payload: string | null): void {
    if (!payload) return;
    navigator.clipboard?.writeText(payload).then(
      () => this.toast.success('Copied to clipboard'),
      () => this.toast.error('Could not copy. Select and copy manually.'),
    );
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
        this.form.reset({ token: '' });
        this.activeTab.set('status');
        this.toast.success(
          s.alreadyActive ? 'This license is already active' : 'License activated',
        );
        this.appInit.refreshLicenseGate();
        // Refresh so a freshly-applied license shows up (and is flagged Current) without the admin
        // having to leave and re-enter the History tab.
        this.loadHistory();
      },
      error: (err: HttpErrorResponse) => {
        this.activating.set(false);
        this.applyError.set(
          err.error?.error_description ?? err.error?.title ?? 'The license token failed verification.'
        );
      },
    });
  }

  // ── Testing/support utilities — never part of the normal apply flow ────────────────────────────

  protected clearLicense(): void {
    if (!confirm('Clear the currently activated license? This cannot be undone.')) { return; }

    this.clearingLicense.set(true);
    this.licenseSvc.clear().subscribe({
      next: (s) => {
        this.clearingLicense.set(false);
        this.status.set(s);
        this.toast.success('License cleared');
        this.appInit.refreshLicenseGate();
      },
      error: (err: HttpErrorResponse) => {
        this.clearingLicense.set(false);
        this.toast.error(err.error?.error_description ?? 'Failed to clear the license.');
      },
    });
  }

  protected clearHistory(): void {
    if (!confirm('Clear all license history? It will disappear from this list immediately; support can restore it later if needed.')) { return; }

    this.clearingHistory.set(true);
    this.licenseSvc.clearHistory().subscribe({
      next: () => {
        this.clearingHistory.set(false);
        this.history.set([]);
        this.toast.success('License history cleared');
      },
      error: (err: HttpErrorResponse) => {
        this.clearingHistory.set(false);
        this.toast.error(err.error?.error_description ?? 'Failed to clear license history.');
      },
    });
  }
}
