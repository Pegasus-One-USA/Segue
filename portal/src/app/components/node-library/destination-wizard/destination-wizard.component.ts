import {
  Component, input, output, signal, computed, effect,
  untracked, inject, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import { DestinationSchemaService, DestinationColumn, DestinationTable } from '../../../services/destination-schema.service';
import { MappingCatalogService, FhirElement, resolveParentReferenceField } from '../../../services/mapping-catalog.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import { DestinationConfigurationDto, DestinationType } from '../../../destination-connections/models/destination-configuration.model';

// ── Resource / field definitions (from HTML prototype) ─────────────────────────

export interface ResourceFieldDef {
  label: string;
  path:  string;
  sqlColumn: string;
  csvColumn: string;
  // Array-aware metadata from the backend FHIR catalog (absent for the built-in fallback defs).
  jsonPath?: string;
  valueType?: string;
  arrays?: string[];
  referenceTargetTypes?: string[];
}

export interface ResourceDef {
  scope:    string;
  sqlTable: string;
  csvFile:  string;
  fields:   ResourceFieldDef[];
}

export const DEST_RESOURCE_DEFS: Record<string, ResourceDef> = {
  Patient: {
    scope: 'user/Patient.read', sqlTable: 'dbo.Patient', csvFile: 'patients.csv',
    fields: [
      { label: 'Patient ID',    path: 'Patient.id',            sqlColumn: 'SourcePatientId', csvColumn: 'PatientId' },
      { label: 'First Name',    path: 'Patient.name.given',    sqlColumn: 'FirstName',       csvColumn: 'FirstName' },
      { label: 'Last Name',     path: 'Patient.name.family',   sqlColumn: 'LastName',        csvColumn: 'LastName' },
      { label: 'Date of Birth', path: 'Patient.birthDate',     sqlColumn: 'DateOfBirth',     csvColumn: 'DateOfBirth' },
      { label: 'Gender',        path: 'Patient.gender',        sqlColumn: 'Gender',          csvColumn: 'Gender' },
      { label: 'Phone',         path: 'Patient.telecom.value', sqlColumn: 'Phone',           csvColumn: 'Phone' },
    ],
  },
  Observation: {
    scope: 'user/Observation.read', sqlTable: 'dbo.PatientObservation', csvFile: 'observations.csv',
    fields: [
      { label: 'Observation ID', path: 'Observation.id',                   sqlColumn: 'SourceObservationId', csvColumn: 'ObservationId' },
      { label: 'Code',           path: 'Observation.code.coding.code',     sqlColumn: 'Code',               csvColumn: 'Code' },
      { label: 'Display',        path: 'Observation.code.coding.display',  sqlColumn: 'Display',            csvColumn: 'Display' },
      { label: 'Value',          path: 'Observation.valueQuantity.value',  sqlColumn: 'Value',              csvColumn: 'Value' },
      { label: 'Unit',           path: 'Observation.valueQuantity.unit',   sqlColumn: 'Unit',               csvColumn: 'Unit' },
      { label: 'Observed At',    path: 'Observation.effectiveDateTime',    sqlColumn: 'ObservedAt',         csvColumn: 'ObservedAt' },
    ],
  },
  Encounter: {
    scope: 'user/Encounter.read', sqlTable: 'dbo.Encounter', csvFile: 'encounters.csv',
    fields: [
      { label: 'Encounter ID',      path: 'Encounter.id',                sqlColumn: 'SourceEncounterId',  csvColumn: 'EncounterId' },
      { label: 'Status',            path: 'Encounter.status',            sqlColumn: 'Status',             csvColumn: 'Status' },
      { label: 'Class',             path: 'Encounter.class.code',        sqlColumn: 'ClassCode',          csvColumn: 'ClassCode' },
      { label: 'Patient Reference', path: 'Encounter.subject.reference', sqlColumn: 'PatientReference',   csvColumn: 'PatientReference' },
      { label: 'Start',             path: 'Encounter.period.start',      sqlColumn: 'StartDate',          csvColumn: 'StartDate' },
      { label: 'End',               path: 'Encounter.period.end',        sqlColumn: 'EndDate',            csvColumn: 'EndDate' },
    ],
  },
  Condition: {
    scope: 'user/Condition.read', sqlTable: 'dbo.Condition', csvFile: 'conditions.csv',
    fields: [
      { label: 'Condition ID',    path: 'Condition.id',                         sqlColumn: 'SourceConditionId', csvColumn: 'ConditionId' },
      { label: 'Code',            path: 'Condition.code.coding.code',           sqlColumn: 'Code',              csvColumn: 'Code' },
      { label: 'Display',         path: 'Condition.code.coding.display',        sqlColumn: 'Display',           csvColumn: 'Display' },
      { label: 'Clinical Status', path: 'Condition.clinicalStatus.coding.code', sqlColumn: 'ClinicalStatus',    csvColumn: 'ClinicalStatus' },
      { label: 'Onset Date',      path: 'Condition.onsetDateTime',              sqlColumn: 'OnsetDate',         csvColumn: 'OnsetDate' },
    ],
  },
  MedicationRequest: {
    scope: 'user/MedicationRequest.read', sqlTable: 'dbo.MedicationRequest', csvFile: 'medication_requests.csv',
    fields: [
      { label: 'Medication ID',   path: 'MedicationRequest.id',                                   sqlColumn: 'SourceMedicationRequestId', csvColumn: 'MedicationRequestId' },
      { label: 'Status',          path: 'MedicationRequest.status',                               sqlColumn: 'Status',                   csvColumn: 'Status' },
      { label: 'Intent',          path: 'MedicationRequest.intent',                               sqlColumn: 'Intent',                   csvColumn: 'Intent' },
      { label: 'Medication Code', path: 'MedicationRequest.medicationCodeableConcept.coding.code', sqlColumn: 'MedicationCode',           csvColumn: 'MedicationCode' },
      { label: 'Requester',       path: 'MedicationRequest.requester.reference',                  sqlColumn: 'RequesterReference',       csvColumn: 'RequesterReference' },
    ],
  },
};

export interface MappingRow {
  resource:   string;
  fieldLabel: string;
  fhirPath:   string;
  targetName: string;
  tableName:  string;
  // Catalog-derived metadata carried through to the build so the mapping engine gets correct,
  // array-aware JSONPaths instead of a guessed conversion.
  jsonPath?:  string;
  valueType?: string;
  arrays?:    string[];
  // True only for a resource's mandatory id row (see _reconcileIdRows) — the column an Upsert write
  // matches an existing row on. Forced/locked by the wizard; never set true on any other row.
  isUpsertKey: boolean;
  // True only for a reference-field row forced/locked by _reconcileParentRefRows because this resource
  // is configured as a child of parentResourceType (see selectedParentsOf/toggleParent). Never
  // removable, never reassignable to another business field — same lock semantics as the id row.
  isRequiredParentRef?: boolean;
  parentResourceType?: string;
}

/** Field set for a resource not in DEST_RESOURCE_DEFS, so any source-selected resource stays mappable. */
function genericResourceDef(r: string): ResourceDef {
  return {
    scope: `user/${r}.read`,
    sqlTable: `dbo.${r}`,
    csvFile: `${r.toLowerCase()}.csv`,
    fields: [
      { label: `${r} ID`, path: `${r}.id`,                sqlColumn: `Source${r}Id`,     csvColumn: `${r}Id` },
      { label: 'Status',  path: `${r}.status`,            sqlColumn: 'Status',           csvColumn: 'Status' },
      { label: 'Subject', path: `${r}.subject.reference`, sqlColumn: 'SubjectReference', csvColumn: 'SubjectReference' },
    ],
  };
}

// ── Component ──────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-destination-wizard',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly catalogSvc = inject(MappingCatalogService);
  private readonly destinationConfigSvc = inject(DestinationConfigurationService);

  // Backend FHIR catalog fields per resource type (array-aware paths). Empty until fetched; the
  // built-in DEST_RESOURCE_DEFS act as the fallback when a resource isn't (yet) loaded.
  private readonly catalogByResource = signal<Record<string, ResourceFieldDef[]>>({});

  readonly destType   = input.required<'sql' | 'csv'>();
  readonly attachNode = input.required<CanvasNode>();
  readonly editNode   = input<CanvasNode | null>(null);
  /** FHIR resource types the upstream source is configured to pull — drives the data-group list (Step 2). */
  readonly sourceResources = input<string[]>([]);

  readonly saved     = output<AddTransformEvent>();
  readonly cancelled = output<void>();

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
    name:      ['SQL Production', [Validators.required]],
    server:    ['', [Validators.required]],
    database:  ['', [Validators.required]],
    auth:      ['sql-auth', [Validators.required]],
    username:  [''],
    password:  [''],
    schema:    ['dbo', []],
    writeMode: ['upsert', []],
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
    emailSubjectTemplate: ['FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}', []],
    emailBodyTemplate:    ['Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.', []],
    // ── Download-link-only field ─────────────────────────────────────────────
    downloadLinkExpiryMinutes: [60, []],
  });

  // ── data groups ───────────────────────────────────────────────────────────
  // The groups offered come from the upstream source's selected resource types when available; otherwise the
  // built-in catalog is the fallback (e.g. a destination added before any source is configured).
  readonly availableGroups = computed(() => {
    const src = this.sourceResources();
    return src.length ? src : Object.keys(DEST_RESOURCE_DEFS);
  });
  readonly selectedResources = signal<string[]>(['Patient', 'Observation', 'Encounter']);

  // Which other selected resources each resource is configured as a "child" of — e.g.
  // { Observation: ['Patient', 'Encounter'] } means Observation independently requires a mapped
  // reference field to both. A resource can have several parents at once (see _reconcileParentRefRows).
  readonly parentSelections = signal<Record<string, string[]>>({});

  // ── mapping rows ──────────────────────────────────────────────────────────
  readonly mappingRows = signal<MappingRow[]>([]);

  // Per-resource destination target (CSV file name / SQL table), entered once in the group header
  // rather than repeated on every mapping row.
  readonly targetByResource = signal<Record<string, string>>({});

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

  // ── computed helpers ──────────────────────────────────────────────────────
  readonly isSql        = computed(() => this.destType() === 'sql');
  readonly destLabel    = computed(() => this.destType() === 'sql' ? 'SQL Server' : 'CSV');
  readonly resourceKeys = computed(() => this.selectedResources());

  /** Resource field/target definition — the built-in catalog entry, or a generic fallback for any other resource. */
  private defFor(r: string): ResourceDef {
    return DEST_RESOURCE_DEFS[r] ?? genericResourceDef(r);
  }

  readonly reviewSummary = computed(() => {
    const fv = this.isSql() ? this.sqlForm.value : this.csvForm.value;
    const rows = this.mappingRows();
    const resources = this.selectedResources();
    return { fv, rows, resources };
  });

  constructor() {
    // Rebuild mapping rows whenever selected resources or destType change
    effect(() => {
      const resources = this.selectedResources();
      const type      = this.destType();
      untracked(() => this._rebuildRows(resources, type));
    });

    // SFTP/email/download-link fields are required only while their mode is selected.
    this._syncDeliveryModeValidators(this.csvForm.controls.deliveryMode.value);
    this.csvForm.controls.deliveryMode.valueChanges.subscribe(v => this._syncDeliveryModeValidators(v));

    effect(() => this.stepChange.emit(this.step()));
    effect(() => this.progressChange.emit(this._hasProgressed()));

    // Fetch the array-aware FHIR catalog for every data group on offer. The field picker prefers it
    // over the built-in fallback once loaded. Deduped via _requested so this effect never re-fetches.
    effect(() => {
      for (const r of this.availableGroups()) this._ensureCatalog(r);
    });

    // Keeps each selected resource's mandatory id/upsert-key row in sync — inserted the moment a resource is
    // selected, upgraded to the schema-verified PK/unique column once the destination table's columns load,
    // and refreshed if the catalog's id field metadata changes. See _reconcileIdRows for the merge rules.
    effect(() => {
      this.selectedResources();
      this.destType();
      this.catalogByResource();
      this.sqlTables();
      this.targetByResource();
      untracked(() => this._reconcileIdRows());
    });

    // Same idea as the id-row effect above, for reference fields required by a "child of" declaration:
    // prunes stale parent selections (a resource or its chosen parent was deselected), then locks/
    // unlocks the corresponding reference-field rows to match.
    effect(() => {
      this.selectedResources();
      this.catalogByResource();
      this.parentSelections();
      untracked(() => {
        this._pruneParentSelections();
        this._reconcileParentRefRows();
      });
    });
  }

  private readonly _requested = new Set<string>();

  private _ensureCatalog(resource: string): void {
    if (this._requested.has(resource)) return;
    this._requested.add(resource);
    this.catalogSvc.fields(resource).subscribe(fields => {
      if (!fields.length) { this._requested.delete(resource); return; }
      const defs = fields.map(f => this._toFieldDef(resource, f));
      this.catalogByResource.update(m => ({ ...m, [resource]: defs }));
    });
  }

  private _toFieldDef(resource: string, f: FhirElement): ResourceFieldDef {
    const column = f.fhirPath.split('.')
      .map(s => s.charAt(0).toUpperCase() + s.slice(1))
      .join('');
    return {
      label: f.label || f.fhirPath,
      path: `${resource}.${f.fhirPath}`,
      sqlColumn: column,
      csvColumn: column,
      jsonPath: f.jsonPath,
      valueType: f.valueType,
      arrays: f.arrays,
      referenceTargetTypes: f.referenceTargetTypes,
    };
  }

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
    // New destination: default the selected data groups to whatever the upstream source pulls, so the destination
    // mirrors the source's Resource Type selection instead of a hardcoded set.
    const src = this.sourceResources();
    if (src.length) this.selectedResources.set([...src]);
  }

  // ── step helpers ──────────────────────────────────────────────────────────
  isStepActive(n: number) { return this.step() === n; }
  isStepDone(n: number)   { return this.step() > n; }

  isNextDisabled(): boolean {
    const s = this.step();
    if (s === 1) {
      if (this.connectionMode() === 'existing' && !this.selectedExistingId()) return true;
      return this.isSql() ? this.sqlForm.invalid : this.csvForm.invalid;
    }
    if (s === 2) return this.selectedResources().length === 0;
    return false;
  }

  // ── navigation ────────────────────────────────────────────────────────────
  next(): void {
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
      destinationType: 'SqlServer',
      server:   v.server   ?? '',
      database: v.database ?? '',
      authentication: v.auth ?? 'sql-auth',
      username: v.username ?? undefined,
      password: v.password ?? undefined,
      trustServerCertificate: true,
      encrypt: true,
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
        this.probeError.set(err?.error?.error ?? err?.message ?? 'Connection failed.');
      },
    });
  }

  // ── select existing connection ───────────────────────────────────────────
  setConnectionMode(mode: 'new' | 'existing'): void {
    this.connectionMode.set(mode);
    if (mode === 'existing' && this.existingOptions().length === 0 && !this.existingOptionsLoading()) {
      this._loadExistingOptions();
    }
    if (!this.isSql()) this._syncDeliveryModeValidators(this.csvForm.value.deliveryMode ?? null);
  }

  private _loadExistingOptions(): void {
    this.existingOptionsLoading.set(true);
    this.destinationConfigSvc
      .getPaged({ isEnabled: true, page: 1, pageSize: 100 })
      .pipe(
        map(page => {
          const wantedTypes = this.isSql() ? DestinationWizardComponent.SQL_TYPES : DestinationWizardComponent.CSV_TYPES;
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
      });
      this._existingBaseline = this.sqlForm.getRawValue();
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
        emailSubjectTemplate: metadata['dest_emailSubjectTemplate'] || 'FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          metadata['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: metadata['dest_downloadLinkExpiryMinutes']
          ? Number(metadata['dest_downloadLinkExpiryMinutes'])
          : 60,
      });
      this._existingBaseline = this.csvForm.getRawValue();
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
    const secretKeys = new Set(['password', 'sftpPassword']);
    const strip = (v: Record<string, unknown>) =>
      Object.fromEntries(Object.entries(v).filter(([key]) => !secretKeys.has(key)));
    const current = this.isSql() ? this.sqlForm.getRawValue() : this.csvForm.getRawValue();
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

  hasSqlTables(): boolean {
    return this.isSql() && this.probeState() === 'ok' && this.sqlTables().length > 0;
  }

  sqlTableOptions(): string[] {
    return this.sqlTables().map(t => t.fullName);
  }

  // Table currently chosen for a resource, with full column metadata (name/type/PK/unique).
  private tableForResourceTarget(r: string): DestinationTable | undefined {
    const target = this.targetFor(r);
    return this.sqlTables().find(t => t.fullName === target || t.tableName === target);
  }

  // Columns of the table currently chosen for a resource (drives the per-row column dropdown).
  columnsForResourceTarget(r: string): string[] {
    return (this.tableForResourceTarget(r)?.columns ?? []).map(c => c.name);
  }

  // ── data groups ───────────────────────────────────────────────────────────
  isResourceSelected(r: string): boolean { return this.selectedResources().includes(r); }

  toggleResource(r: string): void {
    this.selectedResources.update(list =>
      list.includes(r) ? list.filter(x => x !== r) : [...list, r]
    );
  }

  // ── parent-child reference mapping ───────────────────────────────────────
  // Other selected resources that `r` could be a child of — i.e. `r` has at least one FHIR reference
  // field whose allowed target types include that resource. Only these are offered as parent choices,
  // so the UI can never be pushed into a pairing FHIR doesn't actually support.
  candidateParentsFor(r: string): string[] {
    const fields = this.availableFields(r);
    return this.selectedResources().filter(other =>
      other !== r && resolveParentReferenceField(this._asFhirElements(fields), other) !== null);
  }

  selectedParentsOf(r: string): string[] {
    return this.parentSelections()[r] ?? [];
  }

  isParentSelected(r: string, parent: string): boolean {
    return this.selectedParentsOf(r).includes(parent);
  }

  toggleParent(r: string, parent: string): void {
    this.parentSelections.update(m => {
      const current = m[r] ?? [];
      const next = current.includes(parent) ? current.filter(p => p !== parent) : [...current, parent];
      return { ...m, [r]: next };
    });
  }

  private _asFhirElements(fields: ResourceFieldDef[]): FhirElement[] {
    return fields.map(f => {
      const isArray = !!f.arrays?.length;
      return {
        label: f.label,
        jsonPath: f.jsonPath ?? '',
        fhirPath: f.path.includes('.') ? f.path.slice(f.path.indexOf('.') + 1) : f.path,
        cardinality: isArray ? '0..*' : '0..1',
        valueType: f.valueType ?? 'String',
        isArray,
        arrays: f.arrays ?? [],
        referenceTargetTypes: f.referenceTargetTypes ?? [],
      };
    });
  }

  isRequiredParentRefRow(row: MappingRow): boolean {
    return !!row.isRequiredParentRef;
  }

  // ── mapping rows ──────────────────────────────────────────────────────────
  rowsForResource(r: string): MappingRow[] {
    return this.mappingRows().filter(row => row.resource === r);
  }

  updateRow(i: number, field: 'targetName', val: string): void {
    this.mappingRows.update(rows => {
      // The id row's column is auto-resolved (and its dropdown rendered read-only, see isIdColumnLocked in the
      // template) only when a real primary/unique key was auto-matched on the destination table. When none was
      // found, the template renders the id row's column as a live picker instead ("No primary/unique key
      // detected... choose the column") — that picker must actually be able to write here, or the user's
      // selection is silently discarded in favor of the catalog-derived fallback column name.
      if (this.isIdRow(rows[i]) && this.isIdColumnLocked(rows[i].resource)) return rows;
      const next = [...rows];
      next[i] = { ...next[i], [field]: val };
      return next;
    });
  }

  // Adds one empty mapping row for the resource — defaults to the first field
  // not already mapped, so repeated clicks step through the catalog.
  addRow(resource: string): void {
    const fields = this.availableFields(resource);
    if (!fields.length) return;
    const used = new Set(this.rowsForResource(resource).map(r => r.fieldLabel));
    const next = fields.find(f => !used.has(f.label)) ?? fields[0];
    const row: MappingRow = {
      resource,
      fieldLabel: next.label,
      fhirPath:   next.path,
      targetName: this.destType() === 'sql' ? next.sqlColumn : next.csvColumn,
      tableName:  this.targetFor(resource),
      jsonPath:   next.jsonPath,
      valueType:  next.valueType,
      arrays:     next.arrays,
      isUpsertKey: false,
    };
    this.mappingRows.update(rows => [...rows, row]);
  }

  // Bulk "Add Fields": confidence-scores every not-yet-mapped source field against every not-yet-used
  // destination column (name similarity + type compatibility) and appends the best match per field —
  // above a threshold, matched to a real column; below it, added anyway with a blank column for manual
  // pick. The resource's id field is excluded — its row is mandatory and reconciled separately (see
  // _reconcileIdRows), always as the upsert key. Ranks only against the live probed schema, never a
  // hardcoded column list (docs/backend/11-destination-schema-ownership-plan.md section 8).
  private static readonly AUTO_MATCH_THRESHOLD = 0.5;

  addFields(resource: string): void {
    const idField = this.idFieldFor(resource);
    const used = new Set(this.rowsForResource(resource).map(r => r.fieldLabel));
    const remaining = this.availableFields(resource).filter(f =>
      !used.has(f.label) && f.path !== idField?.path);
    if (!remaining.length) return;

    const table = this.tableForResourceTarget(resource);
    const columns = table?.columns ?? [];
    const usedColumns = new Set(this.rowsForResource(resource).map(r => r.targetName).filter(Boolean));

    const newRows: MappingRow[] = [];

    if (!columns.length) {
      // No live schema loaded (CSV, or SQL not yet probed) — fall back to the built-in naming convention,
      // same guess addRow() uses for a single field.
      for (const f of remaining) {
        newRows.push(this._buildRow(resource, f, this.destType() === 'sql' ? f.sqlColumn : f.csvColumn));
      }
    } else {
      const candidates = remaining.flatMap(f =>
        columns
          .filter(c => !usedColumns.has(c.name))
          .map(c => ({ field: f, column: c, score: this._matchScore(f, c) })));
      candidates.sort((a, b) => b.score - a.score);

      // Only fields that cross the confidence threshold get auto-populated. Anything that doesn't match
      // a real column is left out entirely — the admin adds it manually via "+ Add field" instead of the
      // table filling up with unmatched, blank-target rows.
      const assignedFields = new Set<string>();
      const assignedColumns = new Set<string>();
      for (const cand of candidates) {
        if (assignedFields.has(cand.field.label) || assignedColumns.has(cand.column.name)) continue;
        if (cand.score < DestinationWizardComponent.AUTO_MATCH_THRESHOLD) continue;
        assignedFields.add(cand.field.label);
        assignedColumns.add(cand.column.name);
        newRows.push(this._buildRow(resource, cand.field, cand.column.name));
      }
    }

    this.mappingRows.update(rows => [...rows, ...newRows]);
  }

  private _buildRow(resource: string, f: ResourceFieldDef, targetName: string): MappingRow {
    return {
      resource,
      fieldLabel: f.label,
      fhirPath:   f.path,
      targetName,
      tableName:  this.targetFor(resource),
      jsonPath:   f.jsonPath,
      valueType:  f.valueType,
      arrays:     f.arrays,
      isUpsertKey: false,
    };
  }

  private _matchScore(field: ResourceFieldDef, column: DestinationColumn): number {
    const suggested = this.destType() === 'sql' ? field.sqlColumn : field.csvColumn;
    const nameScore = this._nameSimilarity(this._normalize(suggested), this._normalize(column.name))
      || this._nameSimilarity(this._normalize(field.label), this._normalize(column.name));
    const typeScore = field.valueType && field.valueType.toLowerCase() === column.mappingValueType.toLowerCase() ? 1 : 0;
    return nameScore * 0.7 + typeScore * 0.3;
  }

  private _normalize(s: string): string {
    return s.toLowerCase().replace(/[^a-z0-9]/g, '');
  }

  // Levenshtein-distance similarity ratio in [0, 1]; 1 = identical, 0 = nothing in common.
  private _nameSimilarity(a: string, b: string): number {
    if (!a || !b) return 0;
    if (a === b) return 1;
    const dp: number[][] = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
    for (let i = 0; i <= a.length; i++) dp[i][0] = i;
    for (let j = 0; j <= b.length; j++) dp[0][j] = j;
    for (let i = 1; i <= a.length; i++) {
      for (let j = 1; j <= b.length; j++) {
        dp[i][j] = a[i - 1] === b[j - 1]
          ? dp[i - 1][j - 1]
          : 1 + Math.min(dp[i - 1][j - 1], dp[i - 1][j], dp[i][j - 1]);
      }
    }
    return 1 - dp[a.length][b.length] / Math.max(a.length, b.length);
  }

  removeRow(row: MappingRow): void {
    if (this.isIdRow(row)) return; // mandatory — guarantees every resource keeps its upsert key mapped
    if (row.isRequiredParentRef) return; // mandatory while its parent chip is selected — toggle the chip instead
    this.mappingRows.update(rows => {
      const i = rows.indexOf(row);
      if (i < 0) return rows;
      return [...rows.slice(0, i), ...rows.slice(i + 1)];
    });
  }

  // ── mandatory id / upsert-key row ─────────────────────────────────────────
  // Every resource's own `id` field (e.g. Patient.id) must always be mapped and must always be the Upsert
  // key, so two records can never collide/duplicate on write — this can't be turned off or reassigned.
  isIdField(f: ResourceFieldDef, resource: string): boolean {
    return f.path === `${resource}.id`;
  }

  idFieldFor(r: string): ResourceFieldDef | undefined {
    return this.availableFields(r).find(f => this.isIdField(f, r));
  }

  isIdRow(row: MappingRow): boolean {
    return row.fhirPath === `${row.resource}.id`;
  }

  // The schema-verified primary/unique-key column for a resource's live target table, if introspection found
  // one — the only case the id row's destination column is locked/read-only (see isIdColumnLocked below).
  private _autoMatchIdColumn(r: string): string | null {
    const columns = this.tableForResourceTarget(r)?.columns ?? [];
    return columns.find(c => c.isPrimaryKey)?.name ?? columns.find(c => c.isUnique)?.name ?? null;
  }

  // True once the destination column for a resource's id row is schema-verified (a real PK/unique column was
  // found) — only then is it safe to fully lock the field, since a guessed name could otherwise be wrong.
  isIdColumnLocked(r: string): boolean {
    return this._autoMatchIdColumn(r) !== null;
  }

  // Keeps every selected resource's mandatory id row in sync with the live schema and the catalog: inserts it
  // if missing, forces isUpsertKey true, and upgrades its destination column to the schema-verified PK/unique
  // column once one is found (never overwrites a column that isn't schema-verified, so a value loaded from a
  // saved node — or typed in before the schema had a detectable key — is never silently clobbered by a guess).
  private _reconcileIdRows(): void {
    let changed = false;
    let next = [...this.mappingRows()];

    for (const r of this.selectedResources()) {
      const idField = this.idFieldFor(r);
      if (!idField) continue;

      const autoColumn = this._autoMatchIdColumn(r);
      const idx = next.findIndex(row => row.resource === r && this.isIdRow(row));

      if (idx === -1) {
        const initialColumn = autoColumn ?? (this.destType() === 'sql' ? idField.sqlColumn : idField.csvColumn);
        next = [{ ...this._buildRow(r, idField, initialColumn), isUpsertKey: true }, ...next];
        changed = true;
      } else {
        const row = next[idx];
        const desiredColumn = autoColumn ?? row.targetName;
        if (row.targetName !== desiredColumn || !row.isUpsertKey ||
            row.jsonPath !== idField.jsonPath || row.valueType !== idField.valueType) {
          next[idx] = {
            ...row,
            targetName: desiredColumn,
            isUpsertKey: true,
            jsonPath: idField.jsonPath,
            valueType: idField.valueType,
            arrays: idField.arrays,
          };
          changed = true;
        }
      }
    }

    if (changed) this.mappingRows.set(next);
  }

  // Drops parent selections that no longer point at a currently-selected resource, and drops the
  // child side entirely if the child itself was deselected — mirrors the "prune stale state" half of
  // _reconcileIdRows, just for parentSelections instead of mappingRows.
  private _pruneParentSelections(): void {
    const selected = new Set(this.selectedResources());
    this.parentSelections.update(m => {
      let changed = false;
      const next: Record<string, string[]> = {};
      for (const [child, parents] of Object.entries(m)) {
        if (!selected.has(child)) { changed = true; continue; }
        const kept = parents.filter(p => selected.has(p));
        if (kept.length !== parents.length) changed = true;
        if (kept.length) next[child] = kept;
      }
      return changed ? next : m;
    });
  }

  // Keeps each resource's required-reference rows in sync with its current parent selections: one
  // locked row per selected parent (independent — Observation with both Patient and Encounter as
  // parents gets two separate locked rows), inserted/removed/refreshed the same way _reconcileIdRows
  // manages the id row. A parent whose resolver lookup returns null (shouldn't happen, since
  // candidateParentsFor already filters to resolvable pairs) is skipped rather than locking a bad row.
  private _reconcileParentRefRows(): void {
    let changed = false;
    let next = [...this.mappingRows()];

    for (const r of this.selectedResources()) {
      const parents = this.selectedParentsOf(r);
      const wanted = new Set(parents);

      const kept = next.filter(row =>
        !(row.resource === r && row.isRequiredParentRef && !wanted.has(row.parentResourceType ?? '')));
      if (kept.length !== next.length) { next = kept; changed = true; }

      const fields = this.availableFields(r);

      for (const parent of parents) {
        const requiredField = resolveParentReferenceField(this._asFhirElements(fields), parent);
        if (!requiredField) continue;

        const targetFhirPath = `${r}.${requiredField.fhirPath}`;
        const matchingFieldDef = fields.find(f => f.path === targetFhirPath);
        const idx = next.findIndex(row =>
          row.resource === r && row.isRequiredParentRef && row.parentResourceType === parent);

        if (idx === -1) {
          const columnName = matchingFieldDef
            ? (this.destType() === 'sql' ? matchingFieldDef.sqlColumn : matchingFieldDef.csvColumn)
            : requiredField.label;
          const fieldDef: ResourceFieldDef = matchingFieldDef ?? {
            label: requiredField.label,
            path: targetFhirPath,
            sqlColumn: columnName,
            csvColumn: columnName,
            jsonPath: requiredField.jsonPath,
            valueType: requiredField.valueType,
            arrays: requiredField.arrays,
          };
          next = [...next, {
            ...this._buildRow(r, fieldDef, columnName),
            isRequiredParentRef: true,
            parentResourceType: parent,
          }];
          changed = true;
        } else {
          const row = next[idx];
          if (row.fhirPath !== targetFhirPath || row.jsonPath !== requiredField.jsonPath) {
            next[idx] = {
              ...row,
              fhirPath: targetFhirPath,
              fieldLabel: requiredField.label,
              jsonPath: requiredField.jsonPath,
              valueType: requiredField.valueType,
              arrays: requiredField.arrays,
            };
            changed = true;
          }
        }
      }
    }

    if (changed) this.mappingRows.set(next);
  }

  // ── per-resource target (file name / table) ────────────────────────────────
  targetFor(r: string): string { return this.targetByResource()[r] ?? ''; }

  updateTarget(r: string, val: string): void {
    this.targetByResource.update(m => ({ ...m, [r]: val }));
  }

  // ── business-field selection ───────────────────────────────────────────────
  availableFields(r: string): ResourceFieldDef[] {
    // Prefer the array-aware backend catalog; fall back to the built-in defs until it loads (or if offline).
    return this.catalogByResource()[r] ?? this.defFor(r).fields;
  }

  changeBusinessField(i: number, label: string): void {
    this.mappingRows.update(rows => {
      if (this.isIdRow(rows[i])) return rows; // the id row's business field is fixed, never reassignable
      if (rows[i].isRequiredParentRef) return rows; // ditto for a required parent-reference field
      const next = [...rows];
      const row  = next[i];
      const f    = this.availableFields(row.resource).find(x => x.label === label);
      next[i] = {
        ...row,
        fieldLabel: label,
        fhirPath:   f?.path ?? row.fhirPath,
        targetName: f ? (this.destType() === 'sql' ? f.sqlColumn : f.csvColumn) : row.targetName,
        jsonPath:   f?.jsonPath,
        valueType:  f?.valueType,
        arrays:     f?.arrays,
      };
      return next;
    });
  }

  // ── private ───────────────────────────────────────────────────────────────
  // Seeds the per-resource target (file name / table) for newly-selected resources
  // and drops rows for resources the user has deselected. Deliberately does NOT
  // auto-populate field rows — the user adds those one at a time via "+".
  private _rebuildRows(resources: string[], type: 'sql' | 'csv'): void {
    const targets = { ...this.targetByResource() };
    for (const r of resources) {
      if (targets[r]) continue;
      // Seed the per-resource target once; preserve any value the user has already typed.
      const def = this.defFor(r);
      targets[r] = type === 'sql' ? def.sqlTable : def.csvFile;
    }
    this.targetByResource.set(targets);
    this.mappingRows.update(rows =>
      rows
        .filter(row => resources.includes(row.resource))
        .map(row => ({ ...row, tableName: targets[row.resource] ?? row.tableName }))
    );
  }

  private _populateFromNode(node: CanvasNode): void {
    const f = node.fields ?? {};
    if (this.destType() === 'sql') {
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
        emailSubjectTemplate: f['dest_emailSubjectTemplate'] || 'FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}',
        emailBodyTemplate:
          f['dest_emailBodyTemplate'] || 'Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.',
        downloadLinkExpiryMinutes: f['dest_downloadLinkExpiryMinutes'] ? Number(f['dest_downloadLinkExpiryMinutes']) : 60,
      });
    }
    if (f['dest_resources']) {
      this.selectedResources.set(f['dest_resources'].split(',').filter(Boolean));
    }
    if (f['dest_targets']) {
      try { this.targetByResource.set(JSON.parse(f['dest_targets'])); } catch { /* ignore malformed */ }
    }
    if (f['dest_mappings']) {
      try {
        const saved = JSON.parse(f['dest_mappings']) as {
          resource: string; field: string; path: string; target: string; column: string;
          jsonPath?: string; valueType?: string; arrays?: string[]; isUpsertKey?: boolean;
          isRequiredParentRef?: boolean; parentResourceType?: string;
        }[];
        this.mappingRows.set(saved.map(m => ({
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
        })));
      } catch { /* ignore malformed */ }
    }
    if (f['dest_parentSelections']) {
      try { this.parentSelections.set(JSON.parse(f['dest_parentSelections'])); } catch { /* ignore malformed */ }
    }
  }

  private _save(): void {
    const type = this.destType();
    const config: Record<string, string> = {
      dest_resources: this.selectedResources().join(','),
    };

    if (type === 'sql') {
      const v = this.sqlForm.value;
      config['dest_name']      = v.name      ?? '';
      config['dest_server']    = v.server    ?? '';
      config['dest_database']  = v.database  ?? '';
      config['dest_auth']      = v.auth      ?? '';
      config['dest_schema']    = v.schema    ?? 'dbo';
      config['dest_writeMode'] = v.writeMode ?? 'upsert';
      // Persisted so create-on-save can assemble the connection string (server-side it is encrypted at rest via
      // ProvisionedSecrets; the entity only ever stores the secret reference). Only kept for SQL username/password auth.
      if ((v.auth ?? 'sql-auth') === 'sql-auth') {
        config['dest_username'] = v.username ?? '';
        config['dest_password'] = v.password ?? '';
      }
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
    config['dest_mappingCount'] = String(this.mappingRows().length);
    config['dest_targets']  = JSON.stringify(this.targetByResource());
    config['dest_mappings'] = JSON.stringify(
      this.mappingRows().map(r => ({
        resource: r.resource,
        field:    r.fieldLabel,
        path:     r.fhirPath,
        target:   this.targetByResource()[r.resource] ?? r.tableName,
        column:   r.targetName,
        // Array-aware catalog metadata (present for catalog-picked fields) so the build gets the
        // correct JSONPath instead of a guessed conversion.
        jsonPath:  r.jsonPath,
        valueType: r.valueType,
        arrays:    r.arrays,
        isUpsertKey: r.isUpsertKey,
        isRequiredParentRef: r.isRequiredParentRef,
        parentResourceType: r.parentResourceType,
      })),
    );
    config['dest_parentSelections'] = JSON.stringify(this.parentSelections());

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
