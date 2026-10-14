import { Component, inject, OnInit, DestroyRef, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, FormArray, FormGroup } from '@angular/forms';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';
import { SsoConfigurationsService } from '../../services/sso-configurations.service';
import { PingConflictMode, PingNoMappedGroupMode } from '../../models/sso-configurations.model';
import { IRoleService } from '../../../user-management/services/i-role.service';

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
  private readonly roleSvc = inject(IRoleService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly pingSecretConfigured = signal(false);
  protected readonly pingSecretHint = signal<string | null>(null);
  protected readonly pingTesting = signal(false);
  /** Segue roles offered in each mapping row. */
  protected readonly roleOptions = signal<{ value: string; label: string }[]>([]);
  protected readonly pingTestResult = signal<{ success: boolean; message: string } | null>(null);

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
    pingEnabled:                 [false],
    pingEnvironmentId:           [''],
    pingAuthDomain:              ['auth.pingone.com'],
    pingApiDomain:               ['api.pingone.com'],
    pingClientId:                [''],
    pingWorkerClientId:          [''],
    pingWorkerClientSecret:      [''],
    pingJitProvisioning:         [true],
    pingApplyToSaml:             [true],
    pingAutoSwitch:              [true],
    pingMismatchMode:            ['AllowDifferent' as 'AllowDifferent' | 'RestrictMismatch'],
    pingConflictMode:            ['Deny' as PingConflictMode],
    pingNoMappedGroupMode:       ['Deny' as PingNoMappedGroupMode],
    pingMappings:                this.fb.nonNullable.array<FormGroup>([]),
  });

  protected get pingMappings(): FormArray<FormGroup> {
    return this.form.controls.pingMappings as unknown as FormArray<FormGroup>;
  }

  private mappingRow(group = '', role = ''): FormGroup {
    return this.fb.nonNullable.group({ group: [group], role: [role] });
  }

  protected addMapping(): void {
    this.pingMappings.push(this.mappingRow());
    this.form.markAsDirty();
  }

  /** The list order is the priority order, so rows can be moved up or down. */
  protected moveMapping(index: number, delta: -1 | 1): void {
    const target = index + delta;
    if (target < 0 || target >= this.pingMappings.length) return;
    const row = this.pingMappings.at(index);
    this.pingMappings.removeAt(index);
    this.pingMappings.insert(target, row);
    this.form.markAsDirty();
  }

  protected removeMapping(index: number): void {
    this.pingMappings.removeAt(index);
    this.form.markAsDirty();
  }

  constructor() {
    this.unsavedChangesRegistry.register(
      () => this.hasUnsavedChanges(),
      this.destroyRef,
      () => this.isSaveInProgress(),
    );
  }

  ngOnInit(): void {
    // Role names are the stable identifier the API matches on (see UnifiedRoles / role claims).
    this.roleSvc.getRoles().subscribe({
      next: (roles) => this.roleOptions.set(
        roles.map(r => ({ value: r.name, label: r.displayName || r.name })),
      ),
      error: () => this.toast.error('Failed to load Segue roles for the mapping dropdown'),
    });

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
        const ping = settings.ping;
        if (ping) {
          this.form.patchValue({
            pingEnabled:         ping.enabled,
            pingEnvironmentId:   ping.environmentId,
            pingAuthDomain:      ping.authDomain,
            pingApiDomain:       ping.apiDomain,
            pingClientId:        ping.clientId,
            pingWorkerClientId:  ping.workerClientId,
            pingJitProvisioning: ping.jitProvisioning,
            pingApplyToSaml:     ping.applyToSaml,
            pingAutoSwitch:      ping.roleSyncMode === 'AutoSwitch',
            pingMismatchMode:    ping.roleSyncMode === 'RestrictMismatch' ? 'RestrictMismatch' : 'AllowDifferent',
            pingConflictMode:    ping.conflictMode,
            pingNoMappedGroupMode: ping.noMappedGroupMode,
          }, { emitEvent: false });
          this.pingMappings.clear();
          ping.groupRoleMappings.forEach(m => this.pingMappings.push(this.mappingRow(m.group, m.role)));
          this.pingSecretConfigured.set(ping.workerSecretConfigured);
          this.pingSecretHint.set(ping.workerSecretHint ?? null);
        }
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
      ping: {
        enabled:            v.pingEnabled,
        environmentId:      v.pingEnvironmentId.trim(),
        authDomain:         v.pingAuthDomain.trim(),
        apiDomain:          v.pingApiDomain.trim(),
        clientId:           v.pingClientId.trim(),
        workerClientId:     v.pingWorkerClientId.trim(),
        workerClientSecret: v.pingWorkerClientSecret.trim() || null,
        jitProvisioning:    v.pingJitProvisioning,
        applyToSaml:        v.pingApplyToSaml,
        // Row order is the priority order; fully empty rows are dropped, partial ones are left for the API to reject.
        groupRoleMappings:  (v.pingMappings as { group: string; role: string }[])
          .map(m => ({ group: m.group.trim(), role: m.role.trim() }))
          .filter(m => m.group.length > 0 || m.role.length > 0),
        roleSyncMode:       v.pingAutoSwitch ? 'AutoSwitch' : v.pingMismatchMode,
        conflictMode:       v.pingConflictMode,
        noMappedGroupMode:  v.pingNoMappedGroupMode,
      },
    }).subscribe({
      next: (saved) => {
        if (saved.ping) {
          this.pingSecretConfigured.set(saved.ping.workerSecretConfigured);
          this.pingSecretHint.set(saved.ping.workerSecretHint ?? null);
        }
        // The secret is write-only: clear the box once it has been sent.
        this.form.controls.pingWorkerClientSecret.setValue('');
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

  protected testPing(): void {
    this.pingTesting.set(true);
    this.pingTestResult.set(null);
    this.settingsSvc.testPing().subscribe({
      next: (result) => {
        this.pingTestResult.set(result);
        this.pingTesting.set(false);
      },
      error: () => {
        this.pingTestResult.set({ success: false, message: 'The connection test could not be run.' });
        this.pingTesting.set(false);
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
