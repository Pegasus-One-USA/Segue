import { Component, inject, OnInit, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';
import { SsoConfigurationsService } from '../../services/sso-configurations.service';

@Component({
  selector:    'app-sso-configurations',
  standalone:  true,
  imports:     [ReactiveFormsModule],
  templateUrl: './sso-configurations.component.html',
  styleUrl:    './sso-configurations.component.scss',
})
export class SsoConfigurationsComponent implements OnInit, HasUnsavedChanges {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly settingsSvc = inject(SsoConfigurationsService);
  private readonly toast = inject(ToastService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);

  // Read-only — computed by the API from the request's own scheme/host, and status for the one
  // provider that still requires an appsettings edit + restart to change (see form-actions note).
  protected readonly samlMetadataUrl = signal('');
  protected readonly samlAcsUrl = signal('');
  protected readonly googleEnabled = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    samlEnabled:                 [false],
    serviceProviderEntityId:     [''],
    identityProviderEntityId:    [''],
    singleSignOnUrl:             [''],
    identityProviderCertificate: [''],
    portalRedirectUrl:           [''],
    portalErrorRedirectUrl:      [''],
    magicLinkEnabled:            [false],
    entraEnabled:                [false],
    entraInstance:               [''],
    entraTenantId:                [''],
    entraClientId:               [''],
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
          samlEnabled:                 settings.samlEnabled,
          serviceProviderEntityId:     settings.serviceProviderEntityId,
          identityProviderEntityId:    settings.identityProviderEntityId,
          singleSignOnUrl:             settings.singleSignOnUrl,
          identityProviderCertificate: settings.identityProviderCertificate,
          portalRedirectUrl:           settings.portalRedirectUrl,
          portalErrorRedirectUrl:      settings.portalErrorRedirectUrl,
          magicLinkEnabled:            settings.magicLinkEnabled,
          entraEnabled:                settings.entraEnabled,
          entraInstance:               settings.entraInstance,
          entraTenantId:               settings.entraTenantId,
          entraClientId:               settings.entraClientId,
        }, { emitEvent: false });
        this.samlMetadataUrl.set(settings.samlMetadataUrl);
        this.samlAcsUrl.set(settings.samlAcsUrl);
        this.googleEnabled.set(settings.googleEnabled);
        this.form.markAsPristine();
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load SSO configurations');
      },
    });
  }

  protected save(): void {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.saving.set(true);
    const v = this.form.getRawValue();

    this.settingsSvc.update({
      samlEnabled:                 v.samlEnabled,
      serviceProviderEntityId:     v.serviceProviderEntityId.trim(),
      identityProviderEntityId:    v.identityProviderEntityId.trim(),
      singleSignOnUrl:             v.singleSignOnUrl.trim(),
      identityProviderCertificate: v.identityProviderCertificate.trim(),
      portalRedirectUrl:           v.portalRedirectUrl.trim(),
      portalErrorRedirectUrl:      v.portalErrorRedirectUrl.trim(),
      magicLinkEnabled:            v.magicLinkEnabled,
      entraEnabled:                v.entraEnabled,
      entraInstance:               v.entraInstance.trim(),
      entraTenantId:               v.entraTenantId.trim(),
      entraClientId:               v.entraClientId.trim(),
    }).subscribe({
      next: (saved) => {
        this.samlMetadataUrl.set(saved.samlMetadataUrl);
        this.samlAcsUrl.set(saved.samlAcsUrl);
        this.form.markAsPristine();
        this.saving.set(false);
        this.toast.success('SSO configurations saved — changes are live immediately, no restart needed');
      },
      error: (err) => {
        this.saving.set(false);
        const message = err?.error?.title ?? 'Failed to save SSO configurations';
        this.toast.error(message);
      },
    });
  }

  protected copy(value: string): void {
    if (!value) return;
    void navigator.clipboard?.writeText(value);
    this.toast.success('Copied to clipboard');
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    return this.form.dirty;
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }
}
