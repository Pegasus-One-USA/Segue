import {
  Component, input, output, signal, computed, effect,
  inject, viewChild, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import { DestinationSchemaService, DestinationTable } from '../../../services/destination-schema.service';
import { MappingCatalogService } from '../../../services/mapping-catalog.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import { DestinationConfigurationDto, DestinationType } from '../../../destination-connections/models/destination-configuration.model';
import { FHIR_RESOURCES } from '../../../data/scope-constants.data';
import { MappingProfileFormComponent, MappingRow } from './mapping-profile-form.component';

export type { MappingRow };

// ── Component ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-destination-wizard',
  standalone: true,
  imports: [ReactiveFormsModule, MappingProfileFormComponent],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly catalogSvc = inject(MappingCatalogService);
  private readonly destinationConfigSvc = inject(DestinationConfigurationService);

  /** The embedded field-mapping editor (docs/backend/14-mapping-profile-master-screen-plan.md §5.1) — mirrors
   *  DestinationConnectionFormComponent's viewChild()+getConfig() embed pattern used by the Settings screens. */
  readonly mappingForm = viewChild(MappingProfileFormComponent);

  readonly destType   = input.required<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres'>();
  readonly attachNode = input.required<CanvasNode>();
  readonly editNode   = input<CanvasNode | null>(null);
  /** FHIR resource types the upstream source is configured to pull — drives the data-group list (Step 2). */
  readonly sourceResources = input<string[]>([]);

  readonly saved     = output<AddTransformEvent>();
  readonly cancelled = output<void>();
  /** The relocated "✕" next to "← Back to library" — closes the whole Node Library dialog outright
   *  (unlike cancel(), which only backs out of this form to the library's sidebar). Mirrors the
   *  original top-level close button's behavior verbatim: immediate, no unsaved-changes prompt. */
  readonly closeAll  = output<void>();

  // Lets the parent (Node Library sidebar) lock out the other destination type
  // mid-wizard, and warn before discarding progress if the user switches anyway.
  readonly stepChange     = output<number>();
  readonly progressChange = output<boolean>();

  // ── step state ────────────────────────────────────────────────────────────
  readonly step        = signal(1);
  readonly TOTAL_STEPS = 4;
  readonly STEP_LABELS = ['Configure', 'Data groups', 'Map fields', 'Review'];

  // True once the user has advanced past Configure at least once this session —
  // stays true even after going back to step 1, so switching still warns.
  private readonly _hasProgressed = signal(false);

  // ── forms ─────────────────────────────────────────────────────────────────
  readonly sqlForm = this.fb.group({
    name:       ['SQL Production', [Validators.required]],
    server:     ['', [Validators.required]],
    database:   ['', [Validators.required]],
    auth:       ['sql-auth', [Validators.required]],
    username:   [''],
    password:   [''],
    schema:     ['dbo', []],
    writeMode:  ['upsert', []],
    // MySQL/PostgreSQL only (see DestinationConnectionProbeRequest.RequireSsl backend-side): off by default so
    // a local/docker instance with SSL disabled still connects; check for managed providers that enforce SSL
    // (e.g. AWS RDS's rds.force_ssl).
    requireSsl: [false, []],
  });

  readonly mongoForm = this.fb.group({
    name:             ['MongoDB Production', [Validators.required]],
    // Single URI (database embedded, e.g. mongodb://user:pass@host:27017/dbname?authSource=admin) — matches
    // what MappedMongoDestinationWriter expects. Treated as a whole as a secret (see SECRET_FIELD_KEYS): there's
    // no live probe to validate a split server/database/credentials form against, so one opaque field is
    // simplest and avoids a redundant connection-string-assembly step this wizard would otherwise need.
    connectionString: ['', [Validators.required]],
    collection:       ['', [Validators.required]],
    writeMode:        ['upsert', []],
  });

  readonly csvForm = this.fb.group({
    name:         ['CSV Export', [Validators.required]],
    deliveryMode: ['download', [Validators.required]],
    filePattern:  ['{resource}_{yyyyMMdd_HHmmss}.csv', [Validators.required]],
    delimiter:    ['comma', []],
    encoding:     ['utf-8', []],
    // ── SFTP-only connection details ─────────────────────────────────────────
    sftpHost:         ['', []],
    sftpPort:         [22, []],
    sftpUsername:     ['', []],
    sftpAuthType:     ['password', []],
    sftpPassword:     ['', []],
    sftpRemoteFolder: ['', []],
    // ── Email-only fields ─────────────────────────────────────────────────────
    emailTo:              ['', []],
    emailCc:               ['', []],
    emailSubjectTemplate: ['Segue CSV Export - {{RouteName}} - {{RunDate}}', []],
    emailBodyTemplate:    ['Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.', []],
    // ── Download-link-only field ─────────────────────────────────────────────
    downloadLinkExpiryMinutes: [60, []],
  });

  // ── data groups ───────────────────────────────────────────────────────────
  // Always the platform's full curated resource set (FHIR_RESOURCES) — every Epic source now requests scopes
  // for all of these regardless of what's picked here, so this no longer needs to derive from (and be capped
  // by) the specific upstream source's saved resource list, which could also just be stale on older nodes.
  readonly availableGroups = computed(() => FHIR_RESOURCES);
  readonly selectedResources = signal<string[]>([]);

  // ── mapping form seed state ─────────────────────────────────────────────────
  // The embedded MappingProfileFormComponent owns mappingRows/targetByResource/parentSelections going
  // forward; these three only feed its `initial*` inputs once, from _populateFromNode (load-and-edit).
  // A brand-new destination leaves them at their empty defaults, matching the previous behaviour where
  // those signals simply started empty.
  readonly initialMappingRows = signal<MappingRow[]>([]);
  readonly initialTargetByResource = signal<Record<string, string>>({});
  readonly initialParentSelections = signal<Record<string, string[]>>({});

  // ── SQL connection probe (test connection → load tables/columns) ────────────
  readonly sqlTables  = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  // ── select an existing DestinationConfiguration instead of building a new one ───────────────
  // Only offered when attaching a brand-new destination node (not when editing one already on the canvas —
  // that node's fields already pin a connection, existing or otherwise). Excludes destinations that already
  // have pipeline execution history: the workflow-build endpoint re-submits this step's form as an update
  // against the chosen id, which the backend now rejects (409) once a destination has run history, since an
  // update there would silently overwrite a record other routes depend on staying put.
  readonly connectionMode = signal<'new' | 'existing'>('new');
  readonly showConnectionModeToggle = computed(() => !this.editNode());
  readonly existingOptions = signal<DestinationConfigurationDto[]>([]);
  readonly existingOptionsLoading = signal(false);
  readonly selectedExistingId = signal<string | null>(null);

  private static readonly SQL_TYPES: DestinationType[] = ['SqlServer', 'AzureSql', 'PostgreSql', 'MySql'];
  private static readonly CSV_TYPES: DestinationType[] = ['Csv', 'Sftp'];
  private static readonly MONGO_TYPES: DestinationType[] = ['Mongo'];

  // ── computed helpers ──────────────────────────────────────────────────────
  // MySQL/PostgreSQL reuse the SQL family's form/steps (server/database/auth + live table/column introspection) —
  // only the probed destinationType and saved transformId differ from SQL Server. Mongo is its own family:
  // no live introspection, so it gets its own form/branches rather than reusing SQL's or CSV's.
  readonly isSql        = computed(() => this.destType() === 'sql' || this.destType() === 'mysql' || this.destType() === 'postgres');
  readonly isMySql      = computed(() => this.destType() === 'mysql');
  readonly isPostgres   = computed(() => this.destType() === 'postgres');
  readonly isMongo      = computed(() => this.destType() === 'mongo');
  /** MySQL/PostgreSQL only — SQL Server always negotiates encryption regardless, so no SSL toggle for it. */
  readonly showSslToggle = computed(() => this.isMySql() || this.isPostgres());
  readonly isCsv        = computed(() => this.destType() === 'csv');
  readonly destLabel    = computed(() =>
    this.destType() === 'sql' ? 'SQL Server'
      : this.destType() === 'mysql' ? 'MySQL'
      : this.destType() === 'postgres' ? 'PostgreSQL'
      : this.destType() === 'mongo' ? 'MongoDB'
      : 'CSV');
  readonly reviewSummary = computed(() => {
    const fv = this.isSql() ? this.sqlForm.value : this.isMongo() ? this.mongoForm.value : this.csvForm.value;
    const rows = this.mappingForm()?.mappingRows() ?? [];
    const resources = this.selectedResources();
    return { fv, rows, resources };
  });

  constructor() {
    // SFTP/email/download-link fields are required only while their mode is selected.
    this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
    this.csvForm.controls.deliveryMode.valueChanges.subscribe(v => this._syncDeliveryModeValidators(v));

    effect(() => this.stepChange.emit(this.step()));
    effect(() => this.progressChange.emit(this._hasProgressed()));

    // Pre-warms MappingCatalogService's shared cache for every data group on offer, so by the time the user
    // reaches the embedded MappingProfileFormComponent (which fetches per-resource, but only for whichever
    // resources end up selected), most of these requests already resolved in the background. The service is
    // a singleton with its own shareReplay(1) cache per resource, so this and the mapping form's own fetch
    // share one HTTP round trip instead of duplicating it.
    effect(() => {
      for (const r of this.availableGroups()) {
        if (this._prefetchedCatalog.has(r)) continue;
        this._prefetchedCatalog.add(r);
        this.catalogSvc.fields(r).subscribe();
      }
    });
  }

  private readonly _prefetchedCatalog = new Set<string>();

  private _syncDeliveryModeValidators(deliveryMode: string | null): void {
    const isSftp = deliveryMode === 'sftp';
    // sftpPassword is a secret — selectExisting() deliberately never repopulates it (secrets never come back from
    // the API), so requiring it here would permanently block reusing an existing SFTP connection unless the user
    // types something just to satisfy validation. Reusing-as-is never sends a password anywhere (see _save()'s
    // "unchanged" branch), so it doesn't need one; only a genuinely NEW connection (mode 'new', or an edited
    // 'existing' one that forks) does.
    const requirePassword = isSftp && this.connectionMode() === 'new';
    (['sftpHost', 'sftpUsername', 'sftpRemoteFolder'] as const).forEach(name => {
      const ctrl = this.csvForm.get(name)!;
      ctrl.setValidators(isSftp ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
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

  ngOnInit(): void {
    const edit = this.editNode();
    if (edit) {
      this._populateFromNode(edit);
      return;
    }
    // No explicit New/Existing toggle — the "Existing connection" dropdown is just always there, so load its
    // options unconditionally instead of waiting for a "switch to Existing" step that no longer exists.
    if (this.showConnectionModeToggle()) {
      this._loadExistingOptions();
    }
  }

  // ── step helpers ──────────────────────────────────────────────────────────
  isStepActive(n: number) { return this.step() === n; }
  isStepDone(n: number)   { return this.step() > n; }

  isNextDisabled(): boolean {
    const s = this.step();
    if (s === 1) {
      if (this.connectionMode() === 'existing' && !this.selectedExistingId()) return true;
      return this.isSql() ? this.sqlForm.invalid : this.isMongo() ? this.mongoForm.invalid : this.csvForm.invalid;
    }
    if (s === 2) return this.selectedResources().length === 0;
    if (s >= 3) {
      const form = this.mappingForm();
      return !!form && (form.hasUnverifiedColumns() || form.hasTypeMismatchedColumns() || form.resourcesMissingParentSelection().length > 0);
    }
    return false;
  }

  // ── navigation ────────────────────────────────────────────────────────────
  next(): void {
    // The button is only visually dimmed while invalid (see dw-btn--invalid), not hard-disabled — clicking it
    // now reveals exactly which field is missing instead of just silently doing nothing.
    if (this.isNextDisabled()) {
      if (this.step() === 1) {
        (this.isSql() ? this.sqlForm : this.isMongo() ? this.mongoForm : this.csvForm).markAllAsTouched();
      }
      return;
    }
    // SQL: leaving Configure auto-tests the connection and loads tables before advancing.
    if (this.step() === 1 && this.isSql() && this.probeState() !== 'ok') {
      this.testConnection();
      return;
    }
    if (this.step() < this.TOTAL_STEPS) {
      this.step.update(x => x + 1);
      this._hasProgressed.set(true);
    } else {
      this._save();
    }
  }

  back(): void {
    if (this.step() > 1) {
      this.step.update(x => x - 1);
      // Returning to Configure invalidates a prior probe — force a re-test on the next advance.
      if (this.step() === 1 && this.isSql()) { this.probeState.set('idle'); this.sqlTables.set([]); }
    }
  }

  cancel(): void { this.cancelled.emit(); }

  // ── SQL connection test + table/column loading ──────────────────────────────
  testConnection(): void {
    const v = this.sqlForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.probe({
      destinationType: this.isMySql() ? 'MySql' : this.isPostgres() ? 'PostgreSql' : 'SqlServer',
      server:   v.server   ?? '',
      database: v.database ?? '',
      authentication: v.auth ?? 'sql-auth',
      username: v.username ?? undefined,
      password: v.password ?? undefined,
      trustServerCertificate: true,
      encrypt: true,
      requireSsl: v.requireSsl ?? false,
    }).subscribe({
      next: res => {
        if (res.connected) {
          this.sqlTables.set(res.tables);
          this.probeState.set('ok');
          if (this.step() < this.TOTAL_STEPS) {
            this.step.update(x => x + 1);
            this._hasProgressed.set(true);
          }
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

  // ── select existing connection ───────────────────────────────────────────
  /** The "✕" next to the dropdown — undoes a clone and returns the active form to a blank "New" state. This is
   *  the only way back to blank now that there's no explicit New/Existing toggle to switch away from. */
  clearExistingConnection(): void {
    this.connectionMode.set('new');
    this.selectedExistingId.set(null);
    this._existingBaseline = null;
    if (this.isSql()) {
      this.sqlForm.reset();
      this.probeState.set('idle');
      this.sqlTables.set([]);
    } else if (this.isMongo()) {
      this.mongoForm.reset();
    } else {
      this.csvForm.reset();
      this._syncDeliveryModeValidators(this.csvForm.value.deliveryMode ?? null);
    }
  }

  private _loadExistingOptions(): void {
    this.existingOptionsLoading.set(true);
    this.destinationConfigSvc
      .getPaged({ isEnabled: true, page: 1, pageSize: 100 })
      .pipe(
        map(page => {
          const wantedTypes = this.isSql()
            ? DestinationWizardComponent.SQL_TYPES
            : this.isMongo()
              ? DestinationWizardComponent.MONGO_TYPES
              : DestinationWizardComponent.CSV_TYPES;
          return page.items.filter(item => wantedTypes.includes(item.destinationType));
        }),
        switchMap(candidates =>
          candidates.length === 0
            ? of([] as DestinationConfigurationDto[])
            : forkJoin(
                candidates.map(item =>
                  this.destinationConfigSvc.hasExecutionHistory(item.id).pipe(
                    map(res => (res.hasExecutionHistory ? null : item)),
                    catchError(() => of(item)),
                  ),
                ),
              ).pipe(map(results => results.filter((x): x is DestinationConfigurationDto => x !== null))),
        ),
      )
      .subscribe({
        next: options => {
          this.existingOptions.set(options);
          this.existingOptionsLoading.set(false);
        },
        error: () => this.existingOptionsLoading.set(false),
      });
  }

  // Snapshot of the form's raw value taken right after selectExisting() patches it — compared against the current
  // form value at save time (see hasExistingChanged) to decide "reuse as-is" vs "fork a new connection". Secret
  // fields (password/sftpPassword) are part of this snapshot too, at their blank default — a user typing a new
  // secret in counts as a change, same as editing any other field.
  private _existingBaseline: Record<string, unknown> | null = null;

  selectExisting(id: string): void {
    this.connectionMode.set('existing');
    this.selectedExistingId.set(id);
    const selected = this.existingOptions().find(o => o.id === id);
    if (!selected) return;

    const metadata = this._parseConnectionMetadata(selected.connectionMetadataJson);

    if (this.isSql()) {
      this.sqlForm.patchValue({
        name:      metadata['dest_name']      || selected.name,
        server:    metadata['dest_server']    || '',
        database:  metadata['dest_database']  || '',
        auth:      metadata['dest_auth']      || 'sql-auth',
        username:  metadata['dest_username']  || '',
        password:  '',
        schema:    metadata['dest_schema']    || 'dbo',
        writeMode: metadata['dest_writeMode'] || 'upsert',
        requireSsl: metadata['dest_requireSsl'] === 'true',
      });
      this._existingBaseline = this.sqlForm.getRawValue();
    } else if (this.isMongo()) {
      this.mongoForm.patchValue({
        name:             metadata['dest_name']       || selected.name,
        connectionString: '',
        collection:       metadata['dest_collection']  || selected.target || '',
        writeMode:        metadata['dest_writeMode']    || 'upsert',
      });
      this._existingBaseline = this.mongoForm.getRawValue();
    } else {
      this.csvForm.patchValue({
        name:             metadata['dest_name']             || selected.name,
        deliveryMode:     metadata['dest_deliveryMode']     || 'download',
        filePattern:      metadata['dest_filePattern']       || selected.target || this.csvForm.value.filePattern,
        delimiter:        metadata['dest_delimiter']         || 'comma',
        encoding:         metadata['dest_encoding']          || 'utf-8',
        sftpHost:         metadata['dest_sftpHost']          || '',
        sftpPort:         metadata['dest_sftpPort'] ? Number(metadata['dest_sftpPort']) : 22,
        sftpUsername:     metadata['dest_sftpUsername']      || '',
        sftpAuthType:     metadata['dest_sftpAuthType']      || 'password',
        sftpPassword:     '',
        sftpRemoteFolder: metadata['dest_sftpRemoteFolder']  || '',
        emailTo:              metadata['dest_emailTo']              || '',
        emailCc:               metadata['dest_emailCc']               || '',
        emailSubjectTemplate: metadata['dest_emailSubjectTemplate'] || 'Segue CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          metadata['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: metadata['dest_downloadLinkExpiryMinutes']
          ? Number(metadata['dest_downloadLinkExpiryMinutes'])
          : 60,
      });
      this._existingBaseline = this.csvForm.getRawValue();
      this._syncDeliveryModeValidators(this.csvForm.value.deliveryMode ?? null);
    }
  }

  private _parseConnectionMetadata(json: string | null | undefined): Record<string, string> {
    if (!json) return {};
    try {
      const parsed = JSON.parse(json) as unknown;
      return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, string>) : {};
    } catch {
      return {};
    }
  }

  /** True once the user has edited any connection field away from what selectExisting() just patched in — the
   *  save-time signal for "fork a new connection" vs "reuse this one untouched" (see _save()). Secret fields
   *  (password/sftpPassword) are excluded on both sides: selectExisting() always leaves them blank (secrets never
   *  come back from the API), so a real password typed in there to satisfy validation — or just out of habit —
   *  must not by itself count as "changed". Reusing as-is never sends whatever was typed there anywhere; forking
   *  (because something ELSE changed) does use it, same as a brand-new connection. */
  hasExistingChanged(): boolean {
    if (!this._existingBaseline) return false;
    const secretKeys = new Set(['password', 'sftpPassword', 'connectionString']);
    const strip = (v: Record<string, unknown>) =>
      Object.fromEntries(Object.entries(v).filter(([key]) => !secretKeys.has(key)));
    const current = this.isSql() ? this.sqlForm.getRawValue() : this.isMongo() ? this.mongoForm.getRawValue() : this.csvForm.getRawValue();
    return JSON.stringify(strip(current)) !== JSON.stringify(strip(this._existingBaseline));
  }

  /**
   * Resolves a unique name for a forked connection. When the user typed their own distinct name, it's used as-is
   * (forceSuffix false) — only a genuine collision gets a -1/-2/... suffix appended. When the name was left
   * untouched (forceSuffix true, desiredName === the original's own name), a suffix is always appended, since the
   * original itself already holds that exact name.
   */
  private _resolveUniqueName(desiredName: string, forceSuffix: boolean): string {
    const taken = new Set(this.existingOptions().map(o => o.name));
    if (!forceSuffix && !taken.has(desiredName)) return desiredName;
    let suffix = 1;
    let candidate = `${desiredName}-${suffix}`;
    while (taken.has(candidate)) {
      suffix++;
      candidate = `${desiredName}-${suffix}`;
    }
    return candidate;
  }

  // ── data groups ───────────────────────────────────────────────────────────
  isResourceSelected(r: string): boolean { return this.selectedResources().includes(r); }

  toggleResource(r: string): void {
    this.selectedResources.update(list =>
      list.includes(r) ? list.filter(x => x !== r) : [...list, r]
    );
  }

  private _populateFromNode(node: CanvasNode): void {
    const f = node.fields ?? {};
    if (this.isSql()) {
      this.sqlForm.patchValue({
        name:      f['dest_name']      || 'SQL Production',
        server:    f['dest_server']    || '',
        database:  f['dest_database']  || '',
        auth:      f['dest_auth']      || 'managed-identity',
        username:  f['dest_username']  || '',
        password:  f['dest_password']  || '',
        schema:    f['dest_schema']    || 'dbo',
        writeMode: f['dest_writeMode'] || 'upsert',
      });
    } else if (this.isMongo()) {
      this.mongoForm.patchValue({
        name:             f['dest_name']       || 'MongoDB Production',
        connectionString: '',
        collection:       f['dest_collection']  || '',
        writeMode:        f['dest_writeMode']    || 'upsert',
      });
    } else {
      this.csvForm.patchValue({
        name:         f['dest_name']         || 'CSV Export',
        deliveryMode: f['dest_deliveryMode'] || 'download',
        filePattern:  f['dest_filePattern']  || '{resource}_{yyyyMMdd_HHmmss}.csv',
        delimiter:    f['dest_delimiter']    || 'comma',
        encoding:     f['dest_encoding']     || 'utf-8',
        sftpHost:         f['dest_sftpHost']         || '',
        sftpPort:         f['dest_sftpPort'] ? Number(f['dest_sftpPort']) : 22,
        sftpUsername:     f['dest_sftpUsername']     || '',
        sftpAuthType:     f['dest_sftpAuthType']     || 'password',
        sftpPassword:     f['dest_sftpPassword']     || '',
        sftpRemoteFolder: f['dest_sftpRemoteFolder'] || '',
        emailTo:              f['dest_emailTo']              || '',
        emailCc:               f['dest_emailCc']               || '',
        emailSubjectTemplate: f['dest_emailSubjectTemplate'] || 'Segue CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          f['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: f['dest_downloadLinkExpiryMinutes'] ? Number(f['dest_downloadLinkExpiryMinutes']) : 60,
      });
    }
    if (f['dest_resources']) {
      this.selectedResources.set(f['dest_resources'].split(',').filter(Boolean));
    }
    if (f['dest_targets']) {
      try { this.initialTargetByResource.set(JSON.parse(f['dest_targets'])); } catch { /* ignore malformed */ }
    }
    if (f['dest_mappings']) {
      try {
        const saved = JSON.parse(f['dest_mappings']) as {
          resource: string; field: string; path: string; target: string; column: string;
          jsonPath?: string; valueType?: string; arrays?: string[]; isUpsertKey?: boolean;
          isRequiredParentRef?: boolean; parentResourceType?: string;
          isRequired?: boolean; defaultValue?: string; format?: string; normalizationType?: string;
          terminologySystemJsonPath?: string; terminologyCodeJsonPath?: string; arrayPolicy?: string;
          cardinality?: string; correlationCodeJsonPath?: string; correlationCodeValue?: string;
          isEnabled?: boolean;
        }[];
        this.initialMappingRows.set(saved.map(m => ({
          resource:   m.resource,
          fieldLabel: m.field,
          fhirPath:   m.path,
          targetName: m.column,
          tableName:  m.target,
          jsonPath:   m.jsonPath,
          valueType:  m.valueType,
          arrays:     m.arrays,
          isUpsertKey: m.isUpsertKey ?? false,
          isRequiredParentRef: m.isRequiredParentRef ?? false,
          parentResourceType: m.parentResourceType,
          isRequired: m.isRequired,
          defaultValue: m.defaultValue,
          format: m.format,
          normalizationType: m.normalizationType,
          terminologySystemJsonPath: m.terminologySystemJsonPath,
          terminologyCodeJsonPath: m.terminologyCodeJsonPath,
          arrayPolicy: m.arrayPolicy,
          cardinality: m.cardinality,
          correlationCodeJsonPath: m.correlationCodeJsonPath,
          correlationCodeValue: m.correlationCodeValue,
          isEnabled: m.isEnabled,
        })));
      } catch { /* ignore malformed */ }
    }
    if (f['dest_parentSelections']) {
      try { this.initialParentSelections.set(JSON.parse(f['dest_parentSelections'])); } catch { /* ignore malformed */ }
    }
  }

  private _save(): void {
    const type = this.destType();
    const config: Record<string, string> = {
      dest_resources: this.selectedResources().join(','),
    };

    if (type === 'sql' || type === 'mysql' || type === 'postgres') {
      const v = this.sqlForm.value;
      config['dest_name']      = v.name      ?? '';
      config['dest_server']    = v.server    ?? '';
      config['dest_database']  = v.database  ?? '';
      config['dest_auth']      = v.auth      ?? '';
      config['dest_schema']    = v.schema    ?? 'dbo';
      config['dest_writeMode'] = v.writeMode ?? 'upsert';
      config['dest_requireSsl'] = String(v.requireSsl ?? false);
      // Persisted so create-on-save can assemble the connection string (server-side it is encrypted at rest via
      // ProvisionedSecrets; the entity only ever stores the secret reference). Only kept for SQL username/password auth.
      if ((v.auth ?? 'sql-auth') === 'sql-auth') {
        config['dest_username'] = v.username ?? '';
        config['dest_password'] = v.password ?? '';
      }
    } else if (type === 'mongo') {
      const v = this.mongoForm.value;
      config['dest_name']             = v.name             ?? '';
      config['dest_connectionString'] = v.connectionString ?? '';
      config['dest_collection']       = v.collection       ?? '';
      config['dest_writeMode']        = v.writeMode        ?? 'upsert';
    } else {
      const v = this.csvForm.value;
      config['dest_name']         = v.name         ?? '';
      config['dest_deliveryMode'] = v.deliveryMode ?? 'download';
      config['dest_filePattern']  = v.filePattern  ?? '';
      config['dest_delimiter']    = v.delimiter    ?? 'comma';
      config['dest_encoding']     = v.encoding     ?? 'utf-8';
      if (v.deliveryMode === 'sftp') {
        config['dest_sftpHost']         = v.sftpHost         ?? '';
        config['dest_sftpPort']         = String(v.sftpPort  ?? 22);
        config['dest_sftpUsername']     = v.sftpUsername     ?? '';
        config['dest_sftpAuthType']     = v.sftpAuthType     ?? 'password';
        config['dest_sftpPassword']     = v.sftpPassword     ?? '';
        config['dest_sftpRemoteFolder'] = v.sftpRemoteFolder ?? '';
      } else if (v.deliveryMode === 'email') {
        config['dest_emailTo']              = v.emailTo              ?? '';
        config['dest_emailCc']               = v.emailCc               ?? '';
        config['dest_emailSubjectTemplate'] = v.emailSubjectTemplate ?? '';
        config['dest_emailBodyTemplate']    = v.emailBodyTemplate    ?? '';
      } else if (v.deliveryMode === 'downloadUrl') {
        config['dest_downloadLinkExpiryMinutes'] = String(v.downloadLinkExpiryMinutes ?? 60);
      }
    }

    // Reusing an existing DestinationConfiguration — three outcomes depending on what, if anything, the form
    // still differs from what selectExisting() patched in:
    //  - Untouched: wire the node straight to the already-saved record (id + its real secret reference/target).
    //    destinationResolved tells workflow-build-assembler.service.ts to skip this node entirely — no create,
    //    no update, so a connection another workflow also points at can never be mutated by this save.
    //  - Edited, name left alone: fork it as a new, independent connection named "<original>-1" (deduped),
    //    since the original itself already holds the unsuffixed name.
    //  - Edited, name also changed by hand: honor that name as typed (only deduped on an actual collision) rather
    //    than silently suffixing a name the user deliberately chose.
    if (this.connectionMode() === 'existing' && this.selectedExistingId()) {
      const selected = this.existingOptions().find(o => o.id === this.selectedExistingId());
      if (selected && !this.hasExistingChanged()) {
        config['destinationId'] = selected.id;
        config['secretKeyVaultName'] = selected.keyVaultName;
        config['secretName'] = selected.secretName;
        if (selected.target) config['target'] = selected.target;
        config['destinationResolved'] = 'true';
      } else if (selected) {
        const currentName = (config['dest_name'] || selected.name).trim();
        const nameWasEdited = currentName !== selected.name;
        config['dest_name'] = nameWasEdited
          ? this._resolveUniqueName(currentName, false)
          : this._resolveUniqueName(selected.name, true);
      }
    }

    // Persist per-resource targets + the actual field mappings (previously discarded).
    const mappingRows = this.mappingForm()?.mappingRows() ?? [];
    const targetByResource = this.mappingForm()?.targetByResource() ?? {};
    config['dest_mappingCount'] = String(mappingRows.length);
    config['dest_targets']  = JSON.stringify(targetByResource);
    config['dest_mappings'] = JSON.stringify(
      mappingRows.map(r => ({
        resource: r.resource,
        field:    r.fieldLabel,
        path:     r.fhirPath,
        target:   targetByResource[r.resource] ?? r.tableName,
        column:   r.targetName,
        // Array-aware catalog metadata (present for catalog-picked fields) so the build gets the
        // correct JSONPath instead of a guessed conversion.
        jsonPath:  r.jsonPath,
        valueType: r.valueType,
        arrays:    r.arrays,
        isUpsertKey: r.isUpsertKey,
        isRequiredParentRef: r.isRequiredParentRef,
        parentResourceType: r.parentResourceType,
        isRequired: r.isRequired,
        defaultValue: r.defaultValue,
        format: r.format,
        normalizationType: r.normalizationType,
        terminologySystemJsonPath: r.terminologySystemJsonPath,
        terminologyCodeJsonPath: r.terminologyCodeJsonPath,
        arrayPolicy: r.arrayPolicy,
        cardinality: r.cardinality,
        correlationCodeJsonPath: r.correlationCodeJsonPath,
        correlationCodeValue: r.correlationCodeValue,
        isEnabled: r.isEnabled,
      })),
    );
    config['dest_parentSelections'] = JSON.stringify(this.mappingForm()?.parentSelections() ?? {});

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : type === 'mysql' ? 'dest-mysql' : type === 'postgres' ? 'dest-postgres' : type === 'mongo' ? 'dest-mongo' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
