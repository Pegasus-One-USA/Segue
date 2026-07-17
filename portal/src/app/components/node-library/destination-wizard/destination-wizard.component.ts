import {
  Component, input, output, signal, computed, effect,
  untracked, inject, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import { DestinationSchemaService, DestinationTable, DestinationColumn, DestinationProbeRequest } from '../../../services/destination-schema.service';
import { MappingCatalogService, FhirElement } from '../../../services/mapping-catalog.service';
import { FieldMappingCanvasComponent } from './field-mapping/field-mapping-canvas.component';
import { MappingRow, migrateLegacyRow, serializeRowsFlat, LegacyMappingRow } from './field-mapping/field-mapping-model';
import { ToastService } from '../../../services/toast.service';

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
  imports: [ReactiveFormsModule, FieldMappingCanvasComponent],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly catalogSvc = inject(MappingCatalogService);
  private readonly toast = inject(ToastService);

  // Backend FHIR catalog fields per resource type (array-aware paths). Empty until fetched; the
  // built-in DEST_RESOURCE_DEFS act as the fallback when a resource isn't (yet) loaded.
  private readonly catalogByResource = signal<Record<string, ResourceFieldDef[]>>({});

  // Fields derived from a real FHIR JSON payload the user pasted via the canvas's "Load JSON payload"
  // affordance — takes priority over both the backend catalog and the built-in fallback for that
  // resource, since it mirrors data the user actually has rather than a generic field list.
  private readonly payloadFieldsByResource = signal<Record<string, ResourceFieldDef[]>>({});

  readonly destType   = input.required<'sql' | 'csv'>();
  readonly attachNode = input.required<CanvasNode>();
  readonly editNode   = input<CanvasNode | null>(null);
  /** FHIR resource types the upstream source is configured to pull — drives the data-group list (Step 2). */
  readonly sourceResources = input<string[]>([]);
  // Incrementing counters from the parent's header-level Close/Save buttons (shown there instead of
  // the × while a group's mapping canvas is open) — any change triggers the matching action here.
  readonly exitMappingRequest = input<number>(0);
  readonly saveMappingRequest = input<number>(0);
  private _lastExitTrigger = 0;
  private _lastSaveTrigger = 0;

  readonly saved     = output<AddTransformEvent>();
  readonly cancelled = output<void>();

  // Lets the parent (Node Library sidebar) lock out the other destination type
  // mid-wizard, and warn before discarding progress if the user switches anyway.
  readonly stepChange     = output<number>();
  readonly progressChange = output<boolean>();

  // Lets the parent hide its own sidebar while a specific group's mapping canvas is open, so the
  // canvas gets the full dialog width instead of sharing it with the node picker rail.
  readonly mappingCanvasActive = output<boolean>();
  // Lets the parent show "Map fields — {group}" in its own header in place of "Node Library" while
  // the canvas is open — null means "show your normal title", since no group is active.
  readonly mappingCanvasTitle = output<string | null>();

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

  // ── extra target tables (child tables added alongside a group's primary table) ──
  // Keyed by data-group name; each entry is a list of additional already-probed SQL
  // table full-names the user chose to also map into for that same group's canvas
  // (e.g. mapping Patient's Contact array into a real dbo.PatientContact table).
  // Existing tables only — no schema authoring (no "create a new table").
  readonly extraTablesByGroup = signal<Record<string, string[]>>({});

  extraTablesFor(group: string): string[] { return this.extraTablesByGroup()[group] ?? []; }

  /** The canvas computes and emits the new desired list directly (add: appended, remove: filtered) —
   *  this just stores it and cleans up any mappings that targeted a table that's no longer in the list. */
  onExtraTablesChange(tables: string[]): void {
    const g = this.activeMappingGroup();
    if (!g) return;
    const removed = this.extraTablesFor(g).filter(t => !tables.includes(t));
    this.extraTablesByGroup.update(m => ({ ...m, [g]: tables }));
    if (removed.length) {
      this.mappingRows.update(rows => rows.filter(r => !(r.resource === g && removed.includes(r.tableName))));
    }
  }

  /** Column names of any already-probed SQL table by its full name — used for extra target tables. */
  readonly columnsForTableFn = (tableFullName: string): string[] => {
    const table = this.sqlTables().find(t => t.fullName === tableFullName);
    return table ? table.columns.map(c => c.name) : [];
  };

  /** Already-probed tables not yet used as this group's primary or extra targets — offered in "+ Add a table". */
  readonly availableTablesToAddFn = (group: string): string[] => {
    const used = new Set([this.targetFor(group), ...this.extraTablesFor(group)]);
    return this.sqlTables().map(t => t.fullName).filter(t => !used.has(t));
  };

  /** Ad-hoc connection details from Step 1's SQL form — powers the canvas's real ALTER TABLE / CREATE TABLE calls. */
  readonly connectionInfo = computed<DestinationProbeRequest | null>(() => {
    if (!this.isSql()) return null;
    const v = this.sqlForm.value;
    return {
      destinationType: 'SqlServer',
      server: v.server ?? '',
      database: v.database ?? '',
      authentication: v.auth ?? 'sql-auth',
      username: v.username ?? undefined,
      password: v.password ?? undefined,
      trustServerCertificate: true,
      encrypt: true,
    };
  });

  /** A column was really added via the canvas's Add Column modal — append it to the known schema locally. */
  onColumnAdded(e: { tableName: string; column: DestinationColumn }): void {
    this.sqlTables.update(tables => tables.map(t =>
      t.fullName === e.tableName ? { ...t, columns: [...t.columns, e.column] } : t
    ));
  }

  /** A table was really created via the canvas's "+ Add a table" flow — register its known initial schema. */
  onTableCreated(tableFullName: string): void {
    if (this.sqlTables().some(t => t.fullName === tableFullName)) return;
    const dotIndex = tableFullName.indexOf('.');
    const schemaName = dotIndex >= 0 ? tableFullName.slice(0, dotIndex) : 'dbo';
    const tableName = dotIndex >= 0 ? tableFullName.slice(dotIndex + 1) : tableFullName;
    this.sqlTables.update(tables => [...tables, {
      schemaName, tableName, fullName: tableFullName,
      columns: [{ name: 'Id', dataType: 'bigint', mappingValueType: 'Integer', isNullable: false, maxLength: null }],
    }]);
  }

  /** A column was really dropped via the canvas's delete-column flow — remove it from the known schema. */
  onColumnDropped(e: { tableName: string; column: string }): void {
    this.sqlTables.update(tables => tables.map(t =>
      t.fullName === e.tableName ? { ...t, columns: t.columns.filter(c => c.name !== e.column) } : t
    ));
  }

  /** A real FHIR JSON payload was pasted and parsed via the canvas's "Load JSON payload" modal —
   *  store its fields so the source tree for this resource mirrors the payload's actual shape. */
  onSourcePayloadLoaded(e: { resource: string; fields: ResourceFieldDef[] }): void {
    this.payloadFieldsByResource.update(m => ({ ...m, [e.resource]: e.fields }));
  }

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
    effect(() => this.mappingCanvasActive.emit(this.activeMappingGroup() !== null));
    effect(() => {
      const g = this.activeMappingGroup();
      this.mappingCanvasTitle.emit(g ? `Map fields — ${g}` : null);
    });

    // Header-level Close/Save trigger counters — react only on an actual increment, never on the
    // initial read (both start at 0, so the first effect run must not fire either action).
    effect(() => {
      const v = this.exitMappingRequest();
      if (v !== this._lastExitTrigger) { this._lastExitTrigger = v; if (v > 0) this.requestExitMapping(); }
    });
    effect(() => {
      const v = this.saveMappingRequest();
      if (v !== this._lastSaveTrigger) { this._lastSaveTrigger = v; if (v > 0) this.saveGroupMapping(); }
    });

    // Fetch the array-aware FHIR catalog for every data group on offer. The field picker prefers it
    // over the built-in fallback once loaded. Deduped via _requested so this effect never re-fetches.
    effect(() => {
      for (const r of this.availableGroups()) this._ensureCatalog(r);
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
    };
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
      // Leaving Data groups (2) always lands on the group-selection screen, never resuming
      // whichever group's canvas was open last time — even if the user had drilled in before.
      if (this.step() === 2) this.selectedGroupForMapping.set(null);
      this.step.update(x => x + 1);
      this._hasProgressed.set(true);
    } else {
      this._save();
    }
  }

  // ── Step 3: data-group selection screen ────────────────────────────────────
  // Step 3 no longer opens the mapping canvas directly — it first shows a list of the data groups
  // chosen in Step 2, and only opens the (unmodified) canvas, scoped to one resource at a time, once
  // the user clicks "Map" on a row. The canvas's own `resources` input already accepts an arbitrary
  // subset, so scoping it to one group needs no change to the canvas itself.
  private readonly selectedGroupForMapping = signal<string | null>(null);

  // Guards against a stale selection if the user returns to Step 2 and deselects the group they were
  // mapping — falls back to the selection screen rather than showing a canvas for an unselected group.
  readonly activeMappingGroup = computed(() => {
    const g = this.selectedGroupForMapping();
    return g && this.resourceKeys().includes(g) ? g : null;
  });

  // Snapshot of mappingRows/targetByResource taken the moment a group's canvas opens, so "Close" can
  // discard whatever changed during this session (confirmed first) while "Save" just keeps it.
  private mappingRowsSnapshot: MappingRow[] | null = null;
  private targetByResourceSnapshot: Record<string, string> | null = null;
  readonly pendingExitConfirm = signal(false);

  openGroupMapping(resource: string): void {
    this.mappingRowsSnapshot = structuredClone(this.mappingRows());
    this.targetByResourceSnapshot = structuredClone(this.targetByResource());
    this.selectedGroupForMapping.set(resource);
  }

  private closeGroupMapping(): void {
    this.mappingRowsSnapshot = null;
    this.targetByResourceSnapshot = null;
    this.selectedGroupForMapping.set(null);
  }

  /** "Save" below the canvas — keeps whatever's mapped so far and returns to the group list. */
  saveGroupMapping(): void {
    const group = this.activeMappingGroup();
    this.closeGroupMapping();
    if (group) this.toast.success('Mapping saved', `${group} mapping progress saved.`);
  }

  /** "Close" below the canvas — always confirms first, since it discards unsaved changes. */
  requestExitMapping(): void { this.pendingExitConfirm.set(true); }
  cancelExitMapping(): void { this.pendingExitConfirm.set(false); }

  onExitConfirmBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelExitMapping();
  }

  confirmExitMapping(): void {
    if (this.mappingRowsSnapshot) this.mappingRows.set(this.mappingRowsSnapshot);
    if (this.targetByResourceSnapshot) this.targetByResource.set(this.targetByResourceSnapshot);
    this.pendingExitConfirm.set(false);
    this.closeGroupMapping();
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

  // ── per-resource target (file name / table) ────────────────────────────────
  targetFor(r: string): string { return this.targetByResource()[r] ?? ''; }

  updateTarget(r: string, val: string): void {
    this.targetByResource.update(m => ({ ...m, [r]: val }));
  }

  // ── business-field selection ───────────────────────────────────────────────
  availableFields(r: string): ResourceFieldDef[] {
    // Prefer a pasted real payload for this resource; then the array-aware backend catalog; fall back
    // to the built-in defs until the catalog loads (or if offline).
    return this.payloadFieldsByResource()[r] ?? this.catalogByResource()[r] ?? this.defFor(r).fields;
  }

  // Stable references for the field-mapping-canvas's function inputs — declared once so the child
  // component doesn't see a new function identity (and re-render) on every change-detection tick.
  readonly availableFieldsFn = (r: string): ResourceFieldDef[] => this.availableFields(r);
  readonly columnsForResourceTargetFn = (r: string): string[] => this.columnsForResourceTarget(r);

  // ── private ───────────────────────────────────────────────────────────────
  // Seeds the per-resource target (file name / table) for newly-selected resources
  // and drops rows for resources the user has deselected. Deliberately does NOT
  // auto-populate field rows — the user adds those one at a time via "+".
  private _rebuildRows(resources: string[], type: 'sql' | 'csv'): void {
    const oldTargets = this.targetByResource();
    const targets = { ...oldTargets };
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
        .map(row => {
          // Only re-sync rows that were on the resource's OLD primary table — never touch rows on an
          // extra/child table, which the resource's primary-target rename doesn't affect.
          const wasOnPrimary = row.tableName === (oldTargets[row.resource] ?? row.tableName);
          return wasOnPrimary ? { ...row, tableName: targets[row.resource] ?? row.tableName } : row;
        })
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
    if (f['dest_extraTables']) {
      try { this.extraTablesByGroup.set(JSON.parse(f['dest_extraTables'])); } catch { /* ignore malformed */ }
    }
    if (f['dest_sourcePayloadFields']) {
      try { this.payloadFieldsByResource.set(JSON.parse(f['dest_sourcePayloadFields'])); } catch { /* ignore malformed */ }
    }
    if (f['dest_mappings_v2']) {
      try {
        this.mappingRows.set(JSON.parse(f['dest_mappings_v2']) as MappingRow[]);
        return;
      } catch { /* fall through to the legacy loader below */ }
    }
    if (f['dest_mappings']) {
      try {
        const saved = JSON.parse(f['dest_mappings']) as LegacyMappingRow[];
        this.mappingRows.set(saved.map(migrateLegacyRow));
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
    // dest_mappings keeps the legacy flat shape (one entry per row, primary/first source only) so
    // workflow-build-assembler.service.ts keeps working unmodified; dest_mappings_v2 round-trips the
    // full rich shape (joins, instance selection) so re-opening the wizard restores them exactly.
    config['dest_mappingCount'] = String(this.mappingRows().length);
    config['dest_targets']      = JSON.stringify(this.targetByResource());
    config['dest_extraTables']  = JSON.stringify(this.extraTablesByGroup());
    config['dest_sourcePayloadFields'] = JSON.stringify(this.payloadFieldsByResource());
    config['dest_mappings']     = JSON.stringify(serializeRowsFlat(this.mappingRows(), this.targetByResource()));
    config['dest_mappings_v2']  = JSON.stringify(this.mappingRows());

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
