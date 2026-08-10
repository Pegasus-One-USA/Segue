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

  readonly destType = input.required<'sql' | 'csv' | 'fhir'>();
  readonly mode = input<DestinationConnectionFormMode>('create');
  /** dest_* keyed config bag — the same shape DestinationWizardComponent stores on a CanvasNode's fields. */
  readonly initialConfig = input<Record<string, string> | null>(null);

  readonly isSql = computed(() => this.destType() === 'sql');
  readonly isFhir = computed(() => this.destType() === 'fhir');
  readonly isReadOnly = computed(() => this.mode() === 'view');

  readonly sqlForm = this.fb.group({
    name: ['', [Validators.required]],
    engine: ['sqlserver', [Validators.required]],
    server: ['', [Validators.required]],
    database: ['', [Validators.required]],
    auth: ['sql-auth', [Validators.required]],
    username: [''],
    password: [''],
    schema: ['dbo', []],
    writeMode: ['upsert', []],
    requireSsl: [false, []],
  });

  readonly isNonSqlServerEngine = computed(() => this.sqlForm.controls.engine.value !== 'sqlserver');

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
    emailSubjectTemplate: ['Segue CSV Export - {{RouteName}} - {{RunDate}}', []],
    emailBodyTemplate: ['Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.', []],
    // ── Download-link-only field ─────────────────────────────────────────────
    downloadLinkExpiryMinutes: [60, []],
  });

  // Field-for-field identical to DestinationWizardComponent's own fhirForm — duplicated rather than shared
  // (see this component's header comment) so the already-shipped, live-verified canvas wizard is never touched.
  readonly fhirForm = this.fb.group({
    name:          ['Aidbox Production', [Validators.required]],
    baseUrl:       ['', [Validators.required]],
    project:       ['', []],
    authType:      ['oauth2', [Validators.required]],
    writeMode:     ['upsert', []],
    // ── OAuth2 Client Credentials fields (conditional on authType) ───────────
    tokenEndpoint: ['', []],
    clientId:      ['', []],
    clientSecret:  ['', []],
    // ── Basic auth fields (conditional) ──────────────────────────────────────
    username:      ['', []],
    password:      ['', []],
    // ── Bearer token field (conditional) ─────────────────────────────────────
    bearerToken:   ['', []],
  });

  readonly sqlTables = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  constructor() {
    this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
    this.csvForm.controls.deliveryMode.valueChanges.subscribe(v => this._syncDeliveryModeValidators(v));

    this.sqlForm.controls.engine.valueChanges.subscribe(engine => {
      if (engine !== 'sqlserver' && this.sqlForm.controls.auth.value !== 'sql-auth') {
        this.sqlForm.controls.auth.setValue('sql-auth');
      }
    });

    this._syncFhirAuthValidators(this.fhirForm.controls.authType.value);
    this.fhirForm.controls.authType.valueChanges.subscribe(v => this._syncFhirAuthValidators(v));

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
          this.fhirForm.disable({ emitEvent: false });
        } else {
          this.sqlForm.enable({ emitEvent: false });
          this.csvForm.enable({ emitEvent: false });
          this.fhirForm.enable({ emitEvent: false });
          this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
          this._syncFhirAuthValidators(this.fhirForm.controls.authType.value);
        }
      });
    });
  }

  isValid(): boolean {
    return this.isSql() ? this.sqlForm.valid : this.isFhir() ? this.fhirForm.valid : this.csvForm.valid;
  }

  canTestConnection(): boolean {
    if (this.isReadOnly()) return false;
    return this.isSql() || this.isFhir() || this.csvForm.controls.deliveryMode.value === 'sftp';
  }

  testConnection(): void {
    if (this.isSql()) {
      this._testSql();
    } else if (this.isFhir()) {
      this._testFhir();
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
      config['dest_engine'] = v.engine ?? 'sqlserver';
      config['dest_server'] = v.server ?? '';
      config['dest_database'] = v.database ?? '';
      config['dest_auth'] = v.auth ?? '';
      config['dest_schema'] = v.schema ?? 'dbo';
      config['dest_writeMode'] = v.writeMode ?? 'upsert';
      config['dest_requireSsl'] = String(v.requireSsl ?? false);
      if ((v.auth ?? 'sql-auth') === 'sql-auth') {
        config['dest_username'] = v.username ?? '';
        config['dest_password'] = v.password ?? '';
      }
    } else if (this.isFhir()) {
      const v = this.fhirForm.getRawValue();
      config['dest_name'] = v.name ?? '';
      config['dest_baseUrl'] = v.baseUrl ?? '';
      config['dest_project'] = v.project ?? '';
      config['dest_authType'] = v.authType ?? 'oauth2';
      // Mirrors DestinationWizardComponent._save()'s fhir branch exactly: "Bundle" write mode is really two
      // orthogonal backend fields folded into one dropdown (see that component's own doc comment).
      const writeMode = v.writeMode ?? 'upsert';
      config['dest_writeMode'] = writeMode === 'upsertBundle' ? 'upsert' : writeMode;
      config['dest_fhirWriteMode'] = writeMode === 'upsertBundle' ? 'bundle' : 'individual';
      if (v.authType === 'oauth2') {
        config['dest_tokenEndpoint'] = v.tokenEndpoint ?? '';
        config['dest_clientId'] = v.clientId ?? '';
        config['dest_clientSecret'] = v.clientSecret ?? '';
      } else if (v.authType === 'basic') {
        config['dest_username'] = v.username ?? '';
        config['dest_password'] = v.password ?? '';
      } else if (v.authType === 'bearer') {
        config['dest_bearerToken'] = v.bearerToken ?? '';
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
    const engineType = v.engine === 'postgres' ? 'PostgreSql' : v.engine === 'mysql' ? 'MySql' : 'SqlServer';
    this.schemaSvc
      .probe({
        destinationType: engineType,
        server: v.server ?? '',
        database: v.database ?? '',
        authentication: v.auth ?? 'sql-auth',
        username: v.username ?? undefined,
        password: v.password ?? undefined,
        trustServerCertificate: true,
        encrypt: true,
        requireSsl: v.requireSsl ?? false,
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
          this.probeError.set(typeof err?.error?.error === 'string' ? err.error.error : (err?.message ?? 'Connection failed.'));
        },
      });
  }

  // No tokenEndpoint in the request — for oauth2/clientCredentials the backend discovers it from baseUrl via
  // GET {baseUrl}/.well-known/smart-configuration and returns it as resolvedTokenEndpoint, patched into this
  // form's (now-hidden) tokenEndpoint control on success so it still lands in dest_tokenEndpoint at save time.
  private _testFhir(): void {
    const v = this.fhirForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc
      .testFhir({
        baseUrl: v.baseUrl ?? '',
        authType: v.authType ?? 'oauth2',
        clientId: v.clientId ?? undefined,
        clientSecret: v.clientSecret ?? undefined,
        username: v.username ?? undefined,
        password: v.password ?? undefined,
        bearerToken: v.bearerToken ?? undefined,
      })
      .subscribe({
        next: res => {
          if (!res.connected) {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
            return;
          }
          if (res.resolvedTokenEndpoint) {
            this.fhirForm.patchValue({ tokenEndpoint: res.resolvedTokenEndpoint });
          }
          this.probeState.set('ok');
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(typeof err?.error?.error === 'string' ? err.error.error : (err?.message ?? 'Connection failed.'));
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
          this.probeError.set(typeof err?.error?.title === 'string' ? err.error.title : (err?.message ?? 'Connection failed.'));
        },
      });
  }

  private _populateFromConfig(f: Record<string, string>): void {
    if (this.isSql()) {
      this.sqlForm.patchValue({
        name: f['dest_name'] || '',
        engine: f['dest_engine'] || 'sqlserver',
        server: f['dest_server'] || '',
        database: f['dest_database'] || '',
        auth: f['dest_auth'] || 'sql-auth',
        username: f['dest_username'] || '',
        password: f['dest_password'] || '',
        schema: f['dest_schema'] || 'dbo',
        writeMode: f['dest_writeMode'] || 'upsert',
        requireSsl: f['dest_requireSsl'] === 'true',
      });
    } else if (this.isFhir()) {
      const writeMode = f['dest_fhirWriteMode'] === 'bundle' ? 'upsertBundle' : f['dest_writeMode'] || 'upsert';
      this.fhirForm.patchValue({
        name: f['dest_name'] || '',
        baseUrl: f['dest_baseUrl'] || '',
        project: f['dest_project'] || '',
        authType: f['dest_authType'] || 'oauth2',
        writeMode,
        tokenEndpoint: f['dest_tokenEndpoint'] || '',
        clientId: f['dest_clientId'] || '',
        clientSecret: '',
        username: f['dest_username'] || '',
        password: '',
        bearerToken: '',
      });
      this._syncFhirAuthValidators(this.fhirForm.value.authType ?? null);
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
        emailSubjectTemplate: f['dest_emailSubjectTemplate'] || 'Segue CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          f['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: f['dest_downloadLinkExpiryMinutes'] ? Number(f['dest_downloadLinkExpiryMinutes']) : 60,
      });
    }
  }

  private _syncFhirAuthValidators(authType: string | null): void {
    // tokenEndpoint is deliberately NOT in this list — see _testFhir()'s comment.
    (['clientId', 'clientSecret'] as const).forEach(name => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'oauth2' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    (['username', 'password'] as const).forEach(name => {
      const ctrl = this.fhirForm.get(name)!;
      ctrl.setValidators(authType === 'basic' ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    const bearerToken = this.fhirForm.get('bearerToken')!;
    bearerToken.setValidators(authType === 'bearer' ? [Validators.required] : []);
    bearerToken.updateValueAndValidity({ emitEvent: false });
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
