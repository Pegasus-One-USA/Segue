import { Component, inject, OnInit, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';
import { NotificationSettingsService } from '../../services/notification-settings.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';

@Component({
  selector:    'app-email-settings',
  standalone:  true,
  imports:     [ReactiveFormsModule],
  templateUrl: './email-settings.component.html',
  styleUrl:    './email-settings.component.scss',
})
export class EmailSettingsComponent implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly settingsSvc = inject(NotificationSettingsService);
  private readonly toast = inject(ToastService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);
  private readonly permissions = inject(PermissionService);
  private readonly actionGuard = inject(PermissionActionGuard);

  /** This route's own guard allows EITHER configuration.view OR configuration.write (a view-only
   *  role can open the page to see current settings) — unlike Branding, which requires write just to
   *  enter. So this control-level check is the ONLY thing standing between a view-only role and a
   *  fully live Save/Send Test Email button, not defense-in-depth for an already-closed gap. */
  protected readonly canWrite = this.permissions.hasPermission('configuration.write');

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly hasPasswordConfigured = signal(false);

  protected readonly testEmailAddress = signal('');

  /** Password is write-only: blank means "keep the currently saved password" on an edit, but a server
   *  that's never had one configured must not be savable without one — matches username/host/etc. all
   *  being mandatory for an SMTP connection to actually work. Reads hasPasswordConfigured live rather
   *  than being fixed at form-build time — see ngOnInit/save(), which call updateValueAndValidity() on
   *  this control whenever that signal changes so the requirement re-evaluates correctly. */
  private readonly passwordRequiredUnlessAlreadyConfigured = (control: AbstractControl): ValidationErrors | null =>
    this.hasPasswordConfigured() || (control.value ?? '').trim() ? null : { required: true };

  protected readonly form = this.fb.nonNullable.group({
    isEnabled:   [false],
    host:        ['', Validators.required],
    port:        [587, [Validators.required, Validators.min(1), Validators.max(65535)]],
    enableSsl:   [true],
    username:    ['', Validators.required],
    fromAddress: ['', [Validators.required, Validators.email]],
    fromName:    ['Segue', Validators.required],
    // Write-only: blank means "keep the currently saved password". Only sent to the API when non-blank.
    password:    ['', this.passwordRequiredUnlessAlreadyConfigured],
  });

  constructor() {
    this.unsavedChangesRegistry.register(
      () => this.hasUnsavedChanges() || this.isSaveInProgress(),
      this.destroyRef,
    );
  }

  ngOnInit(): void {
    this.settingsSvc.get().subscribe({
      next: (settings) => {
        this.form.patchValue({
          isEnabled:   settings.isEnabled,
          host:        settings.host,
          port:        settings.port,
          enableSsl:   settings.enableSsl,
          username:    settings.username,
          fromAddress: settings.fromAddress,
          fromName:    settings.fromName,
        }, { emitEvent: false });
        this.hasPasswordConfigured.set(settings.hasPasswordConfigured);
        // passwordRequiredUnlessAlreadyConfigured reads this signal, not the control's own value —
        // Angular only re-runs a control's validators on ITS OWN value changes, so this must be
        // triggered explicitly whenever hasPasswordConfigured changes out from under it.
        this.form.controls.password.updateValueAndValidity({ emitEvent: false });
        this.form.markAsPristine();
        if (!this.canWrite) { this.form.disable({ emitEvent: false }); }
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load email settings');
      },
    });
  }

  protected save(): void {
    if (!this.actionGuard.ensure('configuration.write', 'You do not have permission to modify email settings.')) return;
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.saving.set(true);
    const v = this.form.getRawValue();

    this.settingsSvc.update({
      isEnabled:   v.isEnabled,
      host:        v.host.trim(),
      port:        v.port,
      enableSsl:   v.enableSsl,
      username:    v.username.trim(),
      fromAddress: v.fromAddress.trim(),
      fromName:    v.fromName.trim(),
      password:    v.password.trim() || null,
    }).subscribe({
      next: (saved) => {
        this.hasPasswordConfigured.set(saved.hasPasswordConfigured);
        this.form.controls.password.setValue('');
        this.form.markAsPristine();
        this.saving.set(false);
        this.toast.success('Email settings saved');
      },
      error: (err) => {
        this.saving.set(false);
        const message = err?.error?.title ?? 'Failed to save email settings';
        this.toast.error(message);
      },
    });
  }

  protected sendTestEmail(): void {
    if (!this.actionGuard.ensure('configuration.write', 'You do not have permission to send test emails.')) return;
    const toEmail = this.testEmailAddress().trim();
    if (!toEmail) {
      this.toast.error('Enter an email address to send the test to');
      return;
    }
    if (this.form.dirty) {
      this.toast.error('Save your changes first, then send a test email');
      return;
    }

    this.testing.set(true);
    this.settingsSvc.testSend(toEmail).subscribe({
      next: () => {
        this.testing.set(false);
        this.toast.success('Test email sent', `Check ${toEmail}'s inbox.`);
      },
      error: (err) => {
        this.testing.set(false);
        const message = err?.error?.title ?? 'Failed to send the test email';
        this.toast.error(message);
      },
    });
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    return this.form.dirty;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }
}
