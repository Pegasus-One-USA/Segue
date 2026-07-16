import { Component, input, inject, effect, untracked, signal, computed } from '@angular/core';
import { FormBuilder, Validators, ReactiveFormsModule } from '@angular/forms';
import { DestinationSchemaService, DestinationTable } from '../../../services/destination-schema.service';

export type DestinationConnectionFormMode = 'create' | 'edit' | 'view';

/**
 * The Sql/CSV connection fields + Test Connection behavior extracted from DestinationWizardComponent's
 * "Configure" step (step 1) so the standalone Destination Connections admin screen and the workflow canvas
 * wizard render the identical form, rather than two hand-kept-in-sync copies. Deliberately excludes the
 * wizard's data-group/field-mapping steps (2–4) — those belong to a MappingProfile, not a bare
 * DestinationConfiguration record.
 */
@Component({
  selector: 'app-destination-connection-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './destination-connection-form.component.html',
  styleUrls: ['./destination-wizard.component.scss', './destination-connection-form.component.scss'],
})
export class DestinationConnectionFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly destType = input.required<'sql' | 'csv'>();
  readonly mode = input<DestinationConnectionFormMode>('create');
  /** dest_* keyed config bag — the same shape DestinationWizardComponent stores on a CanvasNode's fields. */
  readonly initialConfig = input<Record<string, string> | null>(null);

  readonly isSql = computed(() => this.destType() === 'sql');
  readonly isReadOnly = computed(() => this.mode() === 'view');

  readonly sqlForm = this.fb.group({
    name: ['', [Validators.required]],
    server: ['', [Validators.required]],
    database: ['', [Validators.required]],
    auth: ['sql-auth', [Validators.required]],
    username: [''],
    password: [''],
    schema: ['dbo', []],
    writeMode: ['upsert', []],
  });

  readonly csvForm = this.fb.group({
    name: ['', [Validators.required]],
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

  readonly sqlTables = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  constructor() {
    this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
    this.csvForm.controls.deliveryMode.valueChanges.subscribe(v => this._syncDeliveryModeValidators(v));

    effect(() => {
      const config = this.initialConfig();
      untracked(() => {
        if (config) this._populateFromConfig(config);
      });
    });

    effect(() => {
      const readOnly = this.isReadOnly();
      untracked(() => {
        if (readOnly) {
          this.sqlForm.disable({ emitEvent: false });
          this.csvForm.disable({ emitEvent: false });
        } else {
          this.sqlForm.enable({ emitEvent: false });
          this.csvForm.enable({ emitEvent: false });
          this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
        }
      });
    });
  }

  isValid(): boolean {
    return this.isSql() ? this.sqlForm.valid : this.csvForm.valid;
  }

  canTestConnection(): boolean {
    if (this.isReadOnly()) return false;
    return this.isSql() || this.csvForm.controls.deliveryMode.value === 'sftp';
  }

  testConnection(): void {
    if (this.isSql()) {
      this._testSql();
    } else {
      this._testCsvSftp();
    }
  }

  /** dest_* keyed config bag for the current form, or null if invalid. Mirrors
   * DestinationWizardComponent._save()'s step-1 field mapping (connection fields only). */
  getConfig(): Record<string, string> | null {
    if (!this.isValid()) return null;

    const config: Record<string, string> = {};
    if (this.isSql()) {
      const v = this.sqlForm.getRawValue();
      config['dest_name'] = v.name ?? '';
      config['dest_server'] = v.server ?? '';
      config['dest_database'] = v.database ?? '';
      config['dest_auth'] = v.auth ?? '';
      config['dest_schema'] = v.schema ?? 'dbo';
      config['dest_writeMode'] = v.writeMode ?? 'upsert';
      if ((v.auth ?? 'sql-auth') === 'sql-auth') {
        config['dest_username'] = v.username ?? '';
        config['dest_password'] = v.password ?? '';
      }
    } else {
      const v = this.csvForm.getRawValue();
      config['dest_name'] = v.name ?? '';
      config['dest_deliveryMode'] = v.deliveryMode ?? 'download';
      config['dest_filePattern'] = v.filePattern ?? '';
      config['dest_delimiter'] = v.delimiter ?? 'comma';
      config['dest_encoding'] = v.encoding ?? 'utf-8';
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
    }
    return config;
  }

  private _testSql(): void {
    const v = this.sqlForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .probe({
        destinationType: 'SqlServer',
        server: v.server ?? '',
        database: v.database ?? '',
        authentication: v.auth ?? 'sql-auth',
        username: v.username ?? undefined,
        password: v.password ?? undefined,
        trustServerCertificate: true,
        encrypt: true,
      })
      .subscribe({
        next: res => {
          if (res.connected) {
            this.sqlTables.set(res.tables);
            this.probeState.set('ok');
          } else {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
          }
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(err?.error?.error ?? err?.message ?? 'Connection failed.');
        },
      });
  }

  private _testCsvSftp(): void {
    const v = this.csvForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .testSftp({
        host: v.sftpHost ?? '',
        port: v.sftpPort ?? 22,
        username: v.sftpUsername ?? '',
        password: v.sftpPassword ?? undefined,
        remoteFolder: v.sftpRemoteFolder ?? undefined,
      })
      .subscribe({
        next: res => {
          this.probeState.set(res.connected ? 'ok' : 'error');
          if (!res.connected) this.probeError.set(res.error ?? 'Connection failed.');
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(err?.error?.title ?? err?.message ?? 'Connection failed.');
        },
      });
  }

  private _populateFromConfig(f: Record<string, string>): void {
    if (this.isSql()) {
      this.sqlForm.patchValue({
        name: f['dest_name'] || '',
        server: f['dest_server'] || '',
        database: f['dest_database'] || '',
        auth: f['dest_auth'] || 'sql-auth',
        username: f['dest_username'] || '',
        password: f['dest_password'] || '',
        schema: f['dest_schema'] || 'dbo',
        writeMode: f['dest_writeMode'] || 'upsert',
      });
    } else {
      this.csvForm.patchValue({
        name: f['dest_name'] || '',
        deliveryMode: f['dest_deliveryMode'] || 'download',
        filePattern: f['dest_filePattern'] || '{resource}_{yyyyMMdd_HHmmss}.csv',
        delimiter: f['dest_delimiter'] || 'comma',
        encoding: f['dest_encoding'] || 'utf-8',
        sftpHost: f['dest_sftpHost'] || '',
        sftpPort: f['dest_sftpPort'] ? Number(f['dest_sftpPort']) : 22,
        sftpUsername: f['dest_sftpUsername'] || '',
        sftpAuthType: f['dest_sftpAuthType'] || 'password',
        sftpPassword: f['dest_sftpPassword'] || '',
        sftpRemoteFolder: f['dest_sftpRemoteFolder'] || '',
        emailTo: f['dest_emailTo'] || '',
        emailCc: f['dest_emailCc'] || '',
        emailSubjectTemplate: f['dest_emailSubjectTemplate'] || 'FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          f['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: f['dest_downloadLinkExpiryMinutes'] ? Number(f['dest_downloadLinkExpiryMinutes']) : 60,
      });
    }
  }

  private _syncDeliveryModeValidators(deliveryMode: string | null): void {
    const isSftp = deliveryMode === 'sftp';
    (['sftpHost', 'sftpUsername', 'sftpPassword', 'sftpRemoteFolder'] as const).forEach(name => {
      const ctrl = this.csvForm.get(name)!;
      ctrl.setValidators(isSftp ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
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
