import {
  Component, input, output, signal, computed, effect,
  untracked, inject, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import { DestinationSchemaService, DestinationTable } from '../../../services/destination-schema.service';

// ── Resource / field definitions (from HTML prototype) ─────────────────────────

export interface ResourceFieldDef {
  label: string;
  path:  string;
  sqlColumn: string;
  csvColumn: string;
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
    name:        ['CSV Export', [Validators.required]],
    storageType: ['sftp', [Validators.required]],
    folder:      ['', [Validators.required]],
    filePattern: ['{resource}_{yyyyMMdd_HHmmss}.csv', [Validators.required]],
    delimiter:   ['comma', []],
    encoding:    ['utf-8', []],
    // ── SFTP-only connection details ─────────────────────────────────────────
    sftpHost:         ['', []],
    sftpPort:         [22, []],
    sftpUsername:     ['', []],
    sftpAuthType:     ['password', []],
    sftpPassword:     ['', []],
    sftpRemoteFolder: ['', []],
  });

  // ── data groups ───────────────────────────────────────────────────────────
  // The groups offered come from the upstream source's selected resource types when available; otherwise the
  // built-in catalog is the fallback (e.g. a destination added before any source is configured).
  readonly availableGroups = computed(() => {
    const src = this.sourceResources();
    return src.length ? src : Object.keys(DEST_RESOURCE_DEFS);
  });
  readonly selectedResources = signal<string[]>(['Patient', 'Observation', 'Encounter']);

  // ── mapping rows ──────────────────────────────────────────────────────────
  readonly mappingRows = signal<MappingRow[]>([]);

  // Per-resource destination target (CSV file name / SQL table), entered once in the group header
  // rather than repeated on every mapping row.
  readonly targetByResource = signal<Record<string, string>>({});

  // ── SQL connection probe (test connection → load tables/columns) ────────────
  readonly sqlTables  = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

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

    // SFTP connection fields are required only while Storage type = SFTP.
    this._syncSftpValidators(this.csvForm.controls.storageType.value);
    this.csvForm.controls.storageType.valueChanges.subscribe(v => this._syncSftpValidators(v));

    effect(() => this.stepChange.emit(this.step()));
    effect(() => this.progressChange.emit(this._hasProgressed()));
  }

  private _syncSftpValidators(storageType: string | null): void {
    const isSftp = storageType === 'sftp';
    (['sftpHost', 'sftpUsername', 'sftpPassword', 'sftpRemoteFolder'] as const).forEach(name => {
      const ctrl = this.csvForm.get(name)!;
      ctrl.setValidators(isSftp ? [Validators.required] : []);
      ctrl.updateValueAndValidity({ emitEvent: false });
    });
    const port = this.csvForm.get('sftpPort')!;
    port.setValidators(isSftp ? [Validators.required, Validators.min(1), Validators.max(65535)] : []);
    port.updateValueAndValidity({ emitEvent: false });
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
    if (s === 1) return this.isSql() ? this.sqlForm.invalid : this.csvForm.invalid;
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

  hasSqlTables(): boolean {
    return this.isSql() && this.probeState() === 'ok' && this.sqlTables().length > 0;
  }

  sqlTableOptions(): string[] {
    return this.sqlTables().map(t => t.fullName);
  }

  // Columns of the table currently chosen for a resource (drives the per-row column dropdown).
  columnsForResourceTarget(r: string): string[] {
    const target = this.targetFor(r);
    const table = this.sqlTables().find(t => t.fullName === target || t.tableName === target);
    return table ? table.columns.map(c => c.name) : [];
  }

  // ── data groups ───────────────────────────────────────────────────────────
  isResourceSelected(r: string): boolean { return this.selectedResources().includes(r); }

  toggleResource(r: string): void {
    this.selectedResources.update(list =>
      list.includes(r) ? list.filter(x => x !== r) : [...list, r]
    );
  }

  // ── mapping rows ──────────────────────────────────────────────────────────
  rowsForResource(r: string): MappingRow[] {
    return this.mappingRows().filter(row => row.resource === r);
  }

  updateRow(i: number, field: 'targetName', val: string): void {
    this.mappingRows.update(rows => {
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
    };
    this.mappingRows.update(rows => [...rows, row]);
  }

  removeRow(row: MappingRow): void {
    this.mappingRows.update(rows => {
      const i = rows.indexOf(row);
      if (i < 0) return rows;
      return [...rows.slice(0, i), ...rows.slice(i + 1)];
    });
  }

  // ── per-resource target (file name / table) ────────────────────────────────
  targetFor(r: string): string { return this.targetByResource()[r] ?? ''; }

  updateTarget(r: string, val: string): void {
    this.targetByResource.update(m => ({ ...m, [r]: val }));
  }

  // ── business-field selection ───────────────────────────────────────────────
  availableFields(r: string): ResourceFieldDef[] {
    return this.defFor(r).fields;
  }

  changeBusinessField(i: number, label: string): void {
    this.mappingRows.update(rows => {
      const next = [...rows];
      const row  = next[i];
      const def  = this.defFor(row.resource);
      const f    = def.fields.find(x => x.label === label);
      next[i] = {
        ...row,
        fieldLabel: label,
        fhirPath:   f?.path ?? row.fhirPath,
        targetName: f ? (this.destType() === 'sql' ? f.sqlColumn : f.csvColumn) : row.targetName,
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
        name:        f['dest_name']        || 'CSV Export',
        storageType: f['dest_storageType'] || 'sftp',
        folder:      f['dest_folder']      || '',
        filePattern: f['dest_filePattern'] || '{resource}_{yyyyMMdd_HHmmss}.csv',
        delimiter:   f['dest_delimiter']   || 'comma',
        encoding:    f['dest_encoding']    || 'utf-8',
        sftpHost:         f['dest_sftpHost']         || '',
        sftpPort:         f['dest_sftpPort'] ? Number(f['dest_sftpPort']) : 22,
        sftpUsername:     f['dest_sftpUsername']     || '',
        sftpAuthType:     f['dest_sftpAuthType']     || 'password',
        sftpPassword:     f['dest_sftpPassword']     || '',
        sftpRemoteFolder: f['dest_sftpRemoteFolder'] || '',
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
        const saved = JSON.parse(f['dest_mappings']) as
          { resource: string; field: string; path: string; target: string; column: string }[];
        this.mappingRows.set(saved.map(m => ({
          resource:   m.resource,
          fieldLabel: m.field,
          fhirPath:   m.path,
          targetName: m.column,
          tableName:  m.target,
        })));
      } catch { /* ignore malformed */ }
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
      config['dest_name']        = v.name        ?? '';
      config['dest_storageType'] = v.storageType ?? '';
      config['dest_folder']      = v.folder      ?? '';
      config['dest_filePattern'] = v.filePattern ?? '';
      config['dest_delimiter']   = v.delimiter   ?? 'comma';
      config['dest_encoding']    = v.encoding    ?? 'utf-8';
      if (v.storageType === 'sftp') {
        config['dest_sftpHost']         = v.sftpHost         ?? '';
        config['dest_sftpPort']         = String(v.sftpPort  ?? 22);
        config['dest_sftpUsername']     = v.sftpUsername     ?? '';
        config['dest_sftpAuthType']     = v.sftpAuthType     ?? 'password';
        config['dest_sftpPassword']     = v.sftpPassword     ?? '';
        config['dest_sftpRemoteFolder'] = v.sftpRemoteFolder ?? '';
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
      })),
    );

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
