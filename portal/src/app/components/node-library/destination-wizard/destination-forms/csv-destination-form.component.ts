import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { buildConnectionMetadata, buildSftpUri } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * CSV destination connection form — extracted from DestinationWizardComponent's/DestinationConnectionForm
 * Component's near-identical inline csvForm copies, delivery-mode branching (download/email/sftp/downloadUrl)
 * included and unchanged. SFTP stays one of CSV's own delivery modes here — this is "CSV delivered via SFTP",
 * distinct from the standalone Sftp DestinationType covered by SftpDestinationFormComponent.
 */
@Component({
  selector: 'app-csv-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './csv-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss', './csv-destination-form.component.scss'],
})
export class CsvDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  /** True while the host is reusing a previously-saved connection unchanged (see DestinationWizardComponent's
   *  connectionMode/hasExistingChanged) — sftpPassword is a secret that selectExisting() deliberately never
   *  repopulates (secrets never come back from the API), so requiring it here would permanently block reusing
   *  an existing SFTP connection unless the user types something just to satisfy validation. Reusing as-is
   *  never sends whatever's typed there anywhere, so it doesn't need one; only a genuinely new connection (or
   *  an edited existing one that forks) does. */
  readonly reusingExisting = input<boolean>(false);

  readonly csvForm = this.fb.group({
    name: ['CSV Export', [Validators.required]],
    deliveryMode: ['download', [Validators.required]],
    filePattern: ['{resource}_{yyyyMMdd_HHmmss}.csv', [Validators.required]],
    delimiter: ['comma', []],
    encoding: ['utf-8', []],
    // ── SFTP-only connection details ─────────────────────────────────────────
    sftpHost: ['', []],
    sftpPort: [22, []],
    sftpUsername: ['', []],
    sftpAuthType: ['password', []],
    sftpPassword: ['', []],
    sftpRemoteFolder: ['', []],
    // ── Email-only fields ─────────────────────────────────────────────────────
    emailTo: ['', []],
    emailCc: ['', []],
    emailSubjectTemplate: ['FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}', []],
    emailBodyTemplate: ['Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.', []],
    // ── Download-link-only field ─────────────────────────────────────────────
    downloadLinkExpiryMinutes: [60, []],
  });

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  constructor() {
    this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
    this.csvForm.controls.deliveryMode.valueChanges.subscribe(v => this._syncDeliveryModeValidators(v));
    effect(() => {
      this.reusingExisting();
      untracked(() => this._syncDeliveryModeValidators(this.csvForm.value.deliveryMode ?? null));
    });
  }

  isValid(): boolean {
    return this.csvForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.csvForm.getRawValue();
  }

  canTestConnection(): boolean {
    return this.csvForm.controls.deliveryMode.value === 'sftp';
  }

  testConnection(): void {
    if (!this.canTestConnection()) return;
    const v = this.csvForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.testSftp({
      host: v.sftpHost ?? '',
      port: v.sftpPort ?? 22,
      username: v.sftpUsername ?? '',
      password: v.sftpPassword ?? undefined,
      remoteFolder: v.sftpRemoteFolder ?? undefined,
    }).subscribe({
      next: res => {
        this.probeState.set(res.connected ? 'ok' : 'error');
        if (!res.connected) this.probeError.set(res.error ?? 'Connection failed.');
      },
      error: err => {
        this.probeState.set('error');
        this.probeError.set(typeof err?.error?.title === 'string' ? err.error.title : (err?.message ?? 'Connection failed.'));
      },
    });
  }

  getFullConfig(): Record<string, string> {
    const v = this.csvForm.value;
    const config: Record<string, string> = {
      dest_name: v.name ?? '',
      dest_deliveryMode: v.deliveryMode ?? 'download',
      dest_filePattern: v.filePattern ?? '',
      dest_delimiter: v.delimiter ?? 'comma',
      dest_encoding: v.encoding ?? 'utf-8',
    };
    if (v.deliveryMode === 'sftp') {
      config['dest_sftpHost'] = v.sftpHost ?? '';
      config['dest_sftpPort'] = String(v.sftpPort ?? 22);
      config['dest_sftpUsername'] = v.sftpUsername ?? '';
      config['dest_sftpAuthType'] = v.sftpAuthType ?? 'password';
      config['dest_sftpPassword'] = v.sftpPassword ?? '';
      config['dest_sftpRemoteFolder'] = v.sftpRemoteFolder ?? '';
    } else if (v.deliveryMode === 'email') {
      config['dest_emailTo'] = v.emailTo ?? '';
      config['dest_emailCc'] = v.emailCc ?? '';
      config['dest_emailSubjectTemplate'] = v.emailSubjectTemplate ?? '';
      config['dest_emailBodyTemplate'] = v.emailBodyTemplate ?? '';
    } else if (v.deliveryMode === 'downloadUrl') {
      config['dest_downloadLinkExpiryMinutes'] = String(v.downloadLinkExpiryMinutes ?? 60);
    }
    return config;
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'csv')) as Record<string, string>,
      secret: config['dest_deliveryMode'] === 'sftp' ? buildSftpUri(config) : '',
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.csvForm.patchValue({
      name: fields['dest_name'] || this.csvForm.value.name || 'CSV Export',
      deliveryMode: fields['dest_deliveryMode'] || 'download',
      filePattern: fields['dest_filePattern'] || target || '{resource}_{yyyyMMdd_HHmmss}.csv',
      delimiter: fields['dest_delimiter'] || 'comma',
      encoding: fields['dest_encoding'] || 'utf-8',
      sftpHost: fields['dest_sftpHost'] || '',
      sftpPort: fields['dest_sftpPort'] ? Number(fields['dest_sftpPort']) : 22,
      sftpUsername: fields['dest_sftpUsername'] || '',
      sftpAuthType: fields['dest_sftpAuthType'] || 'password',
      sftpPassword: fields['dest_sftpPassword'] || '',
      sftpRemoteFolder: fields['dest_sftpRemoteFolder'] || '',
      emailTo: fields['dest_emailTo'] || '',
      emailCc: fields['dest_emailCc'] || '',
      emailSubjectTemplate: fields['dest_emailSubjectTemplate'] || 'FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}',
      emailBodyTemplate:
        fields['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
      downloadLinkExpiryMinutes: fields['dest_downloadLinkExpiryMinutes'] ? Number(fields['dest_downloadLinkExpiryMinutes']) : 60,
    });
  }

  reset(): void {
    this.csvForm.reset({
      name: 'CSV Export', deliveryMode: 'download', filePattern: '{resource}_{yyyyMMdd_HHmmss}.csv',
      delimiter: 'comma', encoding: 'utf-8', sftpHost: '', sftpPort: 22, sftpUsername: '', sftpAuthType: 'password',
      sftpPassword: '', sftpRemoteFolder: '', emailTo: '', emailCc: '',
      emailSubjectTemplate: 'FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}',
      emailBodyTemplate: 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
      downloadLinkExpiryMinutes: 60,
    });
    this.probeState.set('idle');
    this.probeError.set(null);
    this._syncDeliveryModeValidators(this.csvForm.value.deliveryMode ?? null);
  }

  /** SFTP/email/download-link fields are required only while their mode is selected. */
  private _syncDeliveryModeValidators(deliveryMode: string | null): void {
    const isSftp = deliveryMode === 'sftp';
    (['sftpHost', 'sftpUsername', 'sftpRemoteFolder'] as const).forEach(name => {
      const ctrl = this.csvForm.get(name)!;
      ctrl.setValidators(isSftp ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    const requirePassword = isSftp && !this.reusingExisting();
    const passwordCtrl = this.csvForm.get('sftpPassword')!;
    passwordCtrl.setValidators(requirePassword ? [Validators.required] : []);
    passwordCtrl.updateValueAndValidity({ emitEvent: false });
    const port = this.csvForm.get('sftpPort')!;
    port.setValidators(isSftp ? [Validators.required, Validators.min(1), Validators.max(65535)] : []);
    port.updateValueAndValidity({ emitEvent: false });

    const isEmail = deliveryMode === 'email';
    const emailTo = this.csvForm.get('emailTo')!;
    emailTo.setValidators(isEmail ? [Validators.required] : []);
    emailTo.updateValueAndValidity({ emitEvent: false });

    const isDownloadUrl = deliveryMode === 'downloadUrl';
    const expiry = this.csvForm.get('downloadLinkExpiryMinutes')!;
    expiry.setValidators(isDownloadUrl ? [Validators.required, Validators.min(1), Validators.max(10080)] : []);
    expiry.updateValueAndValidity({ emitEvent: false });
  }
}
