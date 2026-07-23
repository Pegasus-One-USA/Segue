import { Component, inject, OnInit, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';
import { NotificationSettingsService } from '../../services/notification-settings.service';

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

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly testing = signal(false);
  protected readonly hasPasswordConfigured = signal(false);

  protected readonly testEmailAddress = signal('');

  protected readonly form = this.fb.nonNullable.group({
    isEnabled:   [false],
    host:        ['', Validators.required],
    port:        [587, [Validators.required, Validators.min(1), Validators.max(65535)]],
    enableSsl:   [true],
    username:    [''],
    fromAddress: ['', [Validators.required, Validators.email]],
    fromName:    ['FHIRBridge', Validators.required],
    // Write-only: blank means "keep the currently saved password". Only sent to the API when non-blank.
    password:    [''],
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
        this.form.markAsPristine();
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load email settings');
      },
    });
  }

  protected save(): void {
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
