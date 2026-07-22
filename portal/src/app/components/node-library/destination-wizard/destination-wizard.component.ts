import {
  Component, ElementRef, Injector, input, output, signal, computed, effect,
  untracked, inject, viewChild, afterNextRender, OnInit,
} from '@angular/core';
import {
  FormBuilder, Validators, ReactiveFormsModule,
} from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';
import { CanvasNode } from '../../../models/node.model';
import { AddTransformEvent } from '../node-library-dialog.component';
import { DestinationSchemaService, DestinationTable, DestinationColumn, DestinationProbeRequest } from '../../../services/destination-schema.service';
import { MappingCatalogService, FhirElement } from '../../../services/mapping-catalog.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';
import { CreateDestinationConfigurationRequest, DestinationConfigurationDto, DestinationType } from '../../../destination-connections/models/destination-configuration.model';
import { buildConnectionMetadata, buildSftpUri, buildSqlConnectionString, newSecretName } from '../../../destination-connections/utils/destination-connection-secret.util';
import { FieldMappingCanvasComponent } from './field-mapping/field-mapping-canvas.component';
import { MappingRow, migrateLegacyRow, serializeRowsFlat, LegacyMappingRow } from './field-mapping/field-mapping-model';
import { MappingSnapshotService } from './field-mapping/mapping-snapshot.service';
import { MappingSnapshot, MappingSnapshotSummary } from './field-mapping/mapping-snapshot.model';
import {
  MappingSummaryDocument, ChildTableRelation, buildMappingSummaryDocument, applyMappingSummaryDocument,
} from './field-mapping/field-mapping-summary.model';
import { MappingSummaryService } from './field-mapping/mapping-summary.service';
import { FieldMappingExportPreviewModalComponent } from './field-mapping/field-mapping-export-preview-modal.component';
import { requiredClosureFor, recommendedFor, lockingDependentsOf, sortByDependencyRank, dependencyRankFor } from './resource-dependency.config';
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
  imports: [ReactiveFormsModule, FieldMappingCanvasComponent, FieldMappingExportPreviewModalComponent],
  templateUrl: './destination-wizard.component.html',
  styleUrl: './destination-wizard.component.scss',
})
export class DestinationWizardComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly catalogSvc = inject(MappingCatalogService);
  private readonly toast = inject(ToastService);
  private readonly destinationConfigSvc = inject(DestinationConfigurationService);
  private readonly mappingSnapshotSvc = inject(MappingSnapshotService);
  private readonly mappingSummarySvc = inject(MappingSummaryService);
  private readonly injector = inject(Injector);

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
  /** The upstream source's EHR vendor (e.g. "Epic") — the Mapping JSON's top-level "source" field. */
  readonly sourceVendor = input<string>('');
  /** The pipeline's launch source node's saved connection id — the Mapping JSON's per-resource
   *  "sourceConnectionId" field. Null if no source is wired up yet. */
  readonly sourceConnectionId = input<string | null>(null);
  // Incrementing counters from the parent's header-level Close/Save buttons (shown there instead of
  // the × while a group's mapping canvas is open) — any change triggers the matching action here.
  readonly exitMappingRequest = input<number>(0);
  readonly saveMappingRequest = input<number>(0);
  // Independent of the two above: saves/restores ONLY the mapping-screen state (see MappingSnapshot) —
  // never touches the workflow-level `saved` output, PipelineStore, or node.fields at all.
  readonly saveSnapshotRequest = input<number>(0);
  private _lastExitTrigger = 0;
  private _lastSaveTrigger = 0;
  private _lastSaveSnapshotTrigger = 0;
  // Same pattern, passed straight through to the mapping canvas — its "Load JSON payload"/"Preview
  // output" actions now live in the dialog header (see NodeLibraryDialogComponent), not this canvas's
  // own toolbar, so the wizard just forwards these without reacting to them itself.
  readonly openLoadPayloadRequest = input<number>(0);
  readonly openPreviewRequest = input<number>(0);

  readonly saved     = output<AddTransformEvent>();
  readonly cancelled = output<void>();
  /** Total field-mapping count for the currently open group — the dialog header shows it next to the title. */
  readonly mappingCountChange = output<number>();

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
  readonly selectedResources = signal<string[]>([]);

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

  // ── child-table relations (parent table / parent PK / FK) ───────────────────────────────────────
  // Global, not per-resource/per-canvas — a table's parent/FK relationship doesn't depend on which
  // resource's canvas happens to be open. Previously lived as local state inside
  // FieldMappingCanvasComponent, which meant it was lost the moment a different resource's canvas
  // opened (a fresh component instance) — lifted here so it survives resource navigation, node reload,
  // and the Mapping JSON export/import.
  readonly childTableRelationsByTable = signal<Record<string, ChildTableRelation>>({});

  onChildTableRelationAdded(e: { tableName: string; relation: ChildTableRelation }): void {
    this.childTableRelationsByTable.update(m => ({ ...m, [e.tableName]: e.relation }));
  }

  // ── Mapping JSON — the one canonical save/load contract for this screen (see field-mapping-summary.
  // model.ts). Aggregates every resource with at least one mapping in one shot; independent of
  // dest_mappings/dest_mappings_v2 (still feed workflow-build-assembler.service.ts unmodified). ────────
  readonly lastMappingSummary = signal<MappingSummaryDocument | null>(null);
  readonly mappingSummaryPreviewOpen = signal(false);
  readonly canSaveMappingSummary = computed(() => this.mappingRows().length > 0);

  buildAndShowMappingSummary(): void {
    const doc = buildMappingSummaryDocument({
      sourceVendor: this.sourceVendor().toUpperCase(),
      destType: this.destType(),
      destLabel: this.destLabel(),
      mappingRows: this.mappingRows(),
      sqlTables: this.sqlTables(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
      availableFields: this.availableFieldsFn,
      sourceConnectionId: this.sourceConnectionId(),
      destinationId: this.selectedExistingId() ?? this.resolvedDestinationId(),
    });
    this.lastMappingSummary.set(doc);
    this.mappingSummaryPreviewOpen.set(true);
    this.mappingSummarySvc.save(doc).subscribe(() => {
      this.toast.success('Mapping saved', `${doc.mappings.length} resource mapping${doc.mappings.length === 1 ? '' : 's'} saved.`);
    });
  }

  closeMappingSummaryPreview(): void {
    this.mappingSummaryPreviewOpen.set(false);
  }

  /** Reconstructs this screen entirely from a previously saved Mapping JSON document — "Edit Mapping". */
  loadMappingSummary(doc: MappingSummaryDocument): void {
    const applied = applyMappingSummaryDocument(doc, this.destType());
    this.mappingRows.set(applied.mappingRows);
    this.targetByResource.set(applied.targetByResource);
    this.extraTablesByGroup.set(applied.extraTablesByGroup);
    this.sqlTables.set(applied.sqlTables);
    if (applied.sqlTables.length) this.probeState.set('ok');
    this.childTableRelationsByTable.set(applied.childTableRelationsByTable);
    this.selectedResources.set(applied.selectedResources);
  }

  // ── "Load mapping JSON" — paste a previously saved Mapping JSON document back in. Stubbed the same
  // way MappingSnapshotService is: no real backend endpoint yet, so this only parses/applies pasted
  // text; MappingSummaryService.save()/load() are ready for a real HttpClient call to slot in later. ──
  readonly mappingSummaryLoadText = signal('');
  readonly mappingSummaryLoadError = signal<string | null>(null);

  submitLoadMappingSummary(): void {
    const raw = this.mappingSummaryLoadText().trim();
    if (!raw) { this.mappingSummaryLoadError.set('Paste a Mapping JSON document first.'); return; }
    let doc: MappingSummaryDocument;
    try { doc = JSON.parse(raw) as MappingSummaryDocument; }
    catch (e) { this.mappingSummaryLoadError.set(`Invalid JSON: ${(e as Error).message}`); return; }
    if (!Array.isArray(doc?.mappings)) { this.mappingSummaryLoadError.set('Not a recognized Mapping JSON document (missing "mappings").'); return; }
    this.mappingSummaryLoadError.set(null);
    this.loadMappingSummary(doc);
    this.toast.success('Mapping loaded', `Restored ${doc.mappings.length} resource mapping${doc.mappings.length === 1 ? '' : 's'} from the pasted JSON.`);
  }

  // ── mapping snapshot (independent of the workflow — see mapping-snapshot.model.ts) ─────────────
  // A self-contained save/load for just this screen's state, addressable by its own id, with no
  // dependency on nodes/edges/sources/destinations/trigger/workflowId or a live DB connection to
  // reconstruct. Entirely additive: dest_mappings/dest_mappings_v2-on-the-node (used by the OUTER
  // workflow save, see _save() below) and the existing Save/Close buttons are untouched by this.
  readonly savedSnapshots = signal<MappingSnapshotSummary[]>([]);
  readonly snapshotIdToLoad = signal<string | null>(null);
  readonly snapshotBusy = signal(false);
  /** Set once a snapshot has been loaded this session, so a later Save updates that same record
   *  instead of forking a new one every time. */
  private _loadedSnapshotId: string | null = null;

  refreshSnapshotList(): void {
    this.mappingSnapshotSvc.list().subscribe(list => this.savedSnapshots.set(list));
  }

  private _buildSnapshot(): Omit<MappingSnapshot, 'id' | 'createdAt' | 'updatedAt'> {
    const group = this.activeMappingGroup();
    return {
      name: group ? `${group} mapping` : 'Untitled mapping',
      destType: this.destType(),
      selectedResources: this.selectedResources(),
      activeGroup: group,
      targetByResource: this.targetByResource(),
      extraTablesByGroup: this.extraTablesByGroup(),
      destinationTables: this.sqlTables(),
      payloadFieldsByResource: this.payloadFieldsByResource(),
      mappingRows: this.mappingRows(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
    };
  }

  /** Saves ONLY the mapping-screen state — resource type, destination tables/columns (incl. anything
   *  user-created), mapping rows, and which group/resources are selected. No workflow-level data at all. */
  saveMappingSnapshot(): void {
    this.snapshotBusy.set(true);
    const draft = { ...this._buildSnapshot(), id: this._loadedSnapshotId ?? undefined };
    this.mappingSnapshotSvc.save(draft).subscribe(saved => {
      this._loadedSnapshotId = saved.id;
      this.snapshotBusy.set(false);
      this.refreshSnapshotList();
      this.toast.success('Mapping snapshot saved', `Saved as "${saved.name}" (id: ${saved.id}).`);
    });
  }

  /** Rebuilds the Map Fields screen entirely from a previously saved snapshot — no live DB probe, no
   *  workflow/node context required. */
  loadMappingSnapshot(id: string): void {
    if (!id) return;
    this.snapshotBusy.set(true);
    this.mappingSnapshotSvc.get(id).subscribe(snapshot => {
      this.snapshotBusy.set(false);
      if (!snapshot) { this.toast.error('Load failed', 'That saved mapping could not be found.'); return; }
      this._restoreFromSnapshot(snapshot);
      this.toast.success('Mapping loaded', `Restored "${snapshot.name}".`);
    });
  }

  private _restoreFromSnapshot(s: MappingSnapshot): void {
    this._loadedSnapshotId = s.id;
    this.selectedResources.set(s.selectedResources);
    this.targetByResource.set(s.targetByResource);
    this.extraTablesByGroup.set(s.extraTablesByGroup);
    this.payloadFieldsByResource.set(s.payloadFieldsByResource);
    this.sqlTables.set(s.destinationTables);
    // So hasSqlTables()/sqlTableOptions() behave as if a real probe just succeeded, without one.
    if (s.destinationTables.length) this.probeState.set('ok');
    this.mappingRows.set(s.mappingRows);
    // ?? {} covers snapshots saved before this field existed.
    this.childTableRelationsByTable.set(s.childTableRelationsByTable ?? {});
    this.selectedGroupForMapping.set(s.activeGroup);
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

  /** A column was really added via the canvas's Add Column modal — append it to the known schema locally.
   *  Tagged 'userCreated' so a MappingSnapshot can tell it apart from a probed/pre-existing column.
   *  If `table` is set, the target table didn't already exist and AddColumnAsync auto-created it (e.g. a
   *  resource's default guessed table name, never chosen from a real probed list) — register it as a
   *  brand-new sqlTables() entry instead of trying to append to one that was never there. */
  onColumnAdded(e: { tableName: string; column: DestinationColumn; table?: DestinationTable }): void {
    if (e.table && !this.sqlTables().some(t => t.fullName === e.table!.fullName)) {
      this.sqlTables.update(tables => [...tables, {
        ...e.table!,
        origin: 'userCreated',
        columns: e.table!.columns.map(c => ({ ...c, origin: 'userCreated' })),
      }]);
      return;
    }
    const column: DestinationColumn = { ...e.column, origin: 'userCreated' };
    this.sqlTables.update(tables => tables.map(t =>
      t.fullName === e.tableName ? { ...t, columns: [...t.columns, column] } : t
    ));
  }

  /** A table was really created via the canvas's "Create a new table…" modal — register the exact
   *  schema the backend returned (including its FK column, if this was made a child of a parent table),
   *  rather than guessing one locally. Tagged 'userCreated' (table and all its columns) for the same
   *  reason as onColumnAdded. */
  onTableCreated(table: DestinationTable): void {
    if (this.sqlTables().some(t => t.fullName === table.fullName)) return;
    this.sqlTables.update(tables => [...tables, {
      ...table,
      origin: 'userCreated',
      columns: table.columns.map(c => ({ ...c, origin: 'userCreated' })),
    }]);
  }

  /** A column was really dropped via the canvas's delete-column flow — remove it from the known schema. */
  onColumnDropped(e: { tableName: string; column: string }): void {
    this.sqlTables.update(tables => tables.map(t =>
      t.fullName === e.tableName ? { ...t, columns: t.columns.filter(c => c.name !== e.column) } : t
    ));
  }

  /** A column was really altered (rename and/or data type change) — update its entry in sqlTables()
   *  (preserving its 'userCreated'/'probed' origin) and, if it was renamed, re-point any mapping that
   *  targeted the old column name so it doesn't silently point at a column that no longer exists. */
  onColumnAltered(e: { tableName: string; oldColumnName: string; column: DestinationColumn }): void {
    this.sqlTables.update(tables => tables.map(t =>
      t.fullName === e.tableName
        ? { ...t, columns: t.columns.map(c => c.name === e.oldColumnName ? { ...e.column, origin: c.origin } : c) }
        : t
    ));
    if (e.column.name !== e.oldColumnName) {
      this.mappingRows.update(rows => rows.map(r =>
        r.tableName === e.tableName && r.targetName === e.oldColumnName
          ? { ...r, targetName: e.column.name }
          : r
      ));
    }
  }

  /** A real FHIR JSON payload was pasted and parsed via the canvas's "Load JSON payload" modal —
   *  store its fields so the source tree for this resource mirrors the payload's actual shape. */
  onSourcePayloadLoaded(e: { resource: string; fields: ResourceFieldDef[] }): void {
    this.payloadFieldsByResource.update(m => ({ ...m, [e.resource]: e.fields }));
  }

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

  // Set from the edited node's own fields when a prior build already provisioned a real
  // DestinationConfiguration for it (see workflow-builder.component.ts's stampBuildResultIds) — distinct
  // from selectedExistingId, which only reflects a manual "use an existing connection" pick in Step 1.
  // Also set the moment Step 1 (Configure) is completed in "new connection" mode — see
  // provisionDestinationConnection() — so a real destinationId exists as soon as the connection is
  // configured, not only after the whole wizard finishes and the workflow gets built.
  readonly resolvedDestinationId = signal<string | null>(null);
  readonly provisioningDestination = signal(false);

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
    effect(() => this.mappingCountChange.emit(this.mappingRows().length));

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
    effect(() => {
      const v = this.saveSnapshotRequest();
      if (v !== this._lastSaveSnapshotTrigger) { this._lastSaveSnapshotTrigger = v; if (v > 0) this.saveMappingSnapshot(); }
    });

    // Fetch the array-aware FHIR catalog for every data group on offer. The field picker prefers it
    // over the built-in fallback once loaded. Deduped via _requested, keyed on sourceConnectionId too —
    // so if that only becomes known partway through this session (e.g. after a save/build round-trip
    // stamps a real id onto the source node), this refetches with it instead of staying stuck on
    // whatever (generic-catalog) result was fetched back when it was still null.
    effect(() => {
      const sourceConnectionId = this.sourceConnectionId();
      for (const r of this.availableGroups()) this._ensureCatalog(r, sourceConnectionId);
    });
  }

  private readonly _requested = new Set<string>();

  private _ensureCatalog(resource: string, sourceConnectionId: string | null): void {
    const key = `${resource}::${sourceConnectionId ?? ''}`;
    if (this._requested.has(key)) return;
    this._requested.add(key);
    this.catalogSvc.fields(resource, sourceConnectionId).subscribe(fields => {
      if (!fields.length) { this._requested.delete(key); return; }
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
  }

  ngOnInit(): void {
    this.refreshSnapshotList();
    const edit = this.editNode();
    if (edit) {
      this._populateFromNode(edit);
      return;
    }
    // New destination: no data group is pre-selected — the user picks explicitly, even when an upstream
    // source is connected and could otherwise offer a default.
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
    // SQL: leaving Configure auto-tests the connection and loads tables before advancing; provisioning the
    // real DestinationConfiguration happens inside testConnection()'s success handler, right before it advances.
    if (this.step() === 1 && this.isSql() && this.probeState() !== 'ok') {
      this.testConnection();
      return;
    }
    // CSV: no connection probe gate — provision (create/update) the real DestinationConfiguration here,
    // immediately on leaving Configure, then advance once it succeeds.
    if (this.step() === 1 && !this.isSql()) {
      this.provisionDestinationConnection(() => this._advancePastStep1());
      return;
    }
    if (this.step() < this.TOTAL_STEPS) {
      // Leaving Data groups (2) always lands on the group-selection screen, never resuming
      // whichever group's canvas was open last time — even if the user had drilled in before.
      if (this.step() === 2) {
        this.selectedGroupForMapping.set(null);
        // Map fields (3) lists groups in the order they should actually be run — a prerequisite
        // resource (e.g. Patient) always before whatever requires it — not raw selection-click order.
        const sorted = sortByDependencyRank(this.selectedResources());
        this.selectedResources.set(sorted);
        console.table(sorted.map(resource => ({ resource, rank: dependencyRankFor(resource) })));
      }
      // Leaving Map fields (3) — every resource's mapping is done — log the full, rank-ordered Mapping
      // JSON across every resource that has at least one mapping, so it's there to copy without needing
      // the (hidden-from-this-screen) Save mapping button.
      if (this.step() === 3 && this.canSaveMappingSummary()) {
        const doc = buildMappingSummaryDocument({
          sourceVendor: this.sourceVendor().toUpperCase(),
          destType: this.destType(),
          destLabel: this.destLabel(),
          mappingRows: this.mappingRows(),
          sqlTables: this.sqlTables(),
          childTableRelationsByTable: this.childTableRelationsByTable(),
          availableFields: this.availableFieldsFn,
          sourceConnectionId: this.sourceConnectionId(),
          destinationId: this.selectedExistingId() ?? this.resolvedDestinationId(),
        });
        console.log(JSON.stringify(doc, null, 2));
      }
      this.step.update(x => x + 1);
      this._hasProgressed.set(true);
    } else {
      this._save();
    }
  }

  private _advancePastStep1(): void {
    this.step.set(2);
    this._hasProgressed.set(true);
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

  /** "Save" below the canvas — keeps whatever's mapped so far, returns to the group list, and shows the
   *  canonical Mapping JSON built from every resource mapped so far (not just this one). */
  saveGroupMapping(): void {
    const group = this.activeMappingGroup();
    this.closeGroupMapping();
    if (!group) return;
    if (this.canSaveMappingSummary()) this.buildAndShowMappingSummary();
    else this.toast.success('Mapping saved', `${group} mapping progress saved.`);
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
          // Tagged 'probed' so a MappingSnapshot can tell these apart from anything the user creates
          // afterwards via "+ Add a table"/"+ Add column" (onTableCreated/onColumnAdded, both 'userCreated').
          this.sqlTables.set(res.tables.map(t => ({
            ...t,
            origin: 'probed',
            columns: t.columns.map(c => ({ ...c, origin: 'probed' })),
          })));
          this.probeState.set('ok');
          if (this.step() < this.TOTAL_STEPS) {
            this.provisionDestinationConnection(() => this._advancePastStep1());
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
    if (!this.isSql()) this._syncSftpValidators(this.csvForm.value.storageType ?? null);
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
        storageType:      metadata['dest_storageType']      || 'sftp',
        folder:           metadata['dest_folder']            || '',
        filePattern:      metadata['dest_filePattern']       || selected.target || this.csvForm.value.filePattern,
        delimiter:        metadata['dest_delimiter']         || 'comma',
        encoding:         metadata['dest_encoding']          || 'utf-8',
        sftpHost:         metadata['dest_sftpHost']          || '',
        sftpPort:         metadata['dest_sftpPort'] ? Number(metadata['dest_sftpPort']) : 22,
        sftpUsername:     metadata['dest_sftpUsername']      || '',
        sftpAuthType:     metadata['dest_sftpAuthType']      || 'password',
        sftpPassword:     '',
        sftpRemoteFolder: metadata['dest_sftpRemoteFolder']  || '',
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

  // Columns of the table currently chosen for a resource (drives the per-row column dropdown).
  columnsForResourceTarget(r: string): string[] {
    const target = this.targetFor(r);
    const table = this.sqlTables().find(t => t.fullName === target || t.tableName === target);
    return table ? table.columns.map(c => c.name) : [];
  }

  /** Real data type (e.g. "nvarchar(50)") of one column on any already-known SQL table — undefined for
   *  CSV destinations or free-text/pending columns that have no real schema behind them yet. Purely a
   *  display concern for the mapping canvas's cards; column identity everywhere else stays the name. */
  dataTypeForTableColumn(tableFullName: string, column: string): string | undefined {
    const table = this.sqlTables().find(t => t.fullName === tableFullName || t.tableName === tableFullName);
    return table?.columns.find(c => c.name === column)?.dataType;
  }

  // ── data groups ───────────────────────────────────────────────────────────
  isResourceSelected(r: string): boolean { return this.selectedResources().includes(r); }

  /** Currently-selected resources that transitively require `r` — non-empty means `r` can't be
   *  deselected right now (see resource-dependency.config.ts). */
  lockingDependentsFor(r: string): string[] { return lockingDependentsOf(r, this.selectedResources()); }

  isResourceLocked(r: string): boolean {
    return this.isResourceSelected(r) && this.lockingDependentsFor(r).length > 0;
  }

  /** Currently-selected resources that recommend (but don't require) `r` — only meaningful while `r`
   *  itself isn't selected yet, since a selected resource has nothing left to be recommended for. */
  private recommendedByFor(r: string): string[] {
    if (this.isResourceSelected(r)) return [];
    return this.selectedResources().filter(sel => recommendedFor(sel).includes(r));
  }

  /** Small explanatory line rendered under a resource card: why it was auto-selected (required by …),
   *  or why it's suggested (recommended alongside …). Null when neither applies. */
  resourceHintFor(r: string): string | null {
    if (this.isResourceSelected(r)) {
      const dependents = this.lockingDependentsFor(r);
      return dependents.length ? `Required by ${dependents.join(', ')}` : null;
    }
    const suggestedBy = this.recommendedByFor(r);
    return suggestedBy.length ? `Recommended alongside ${suggestedBy.join(', ')}` : null;
  }

  toggleResource(r: string): void {
    if (this.isResourceSelected(r)) {
      const dependents = this.lockingDependentsFor(r);
      if (dependents.length) {
        this.toast.warning(
          'Required resource',
          `${r} is required by ${dependents.join(', ')} — remove ${dependents.length > 1 ? 'them' : 'it'} first.`,
        );
        return;
      }
      this.selectedResources.update(list => list.filter(x => x !== r));
      return;
    }

    // Auto-select this resource's required dependencies too (transitively) — but only ones actually
    // offered as a card here; a dependency the current catalog doesn't offer has nowhere to render.
    const available = this.availableGroups();
    const current = this.selectedResources();
    const autoAdded = requiredClosureFor(r).filter(dep => available.includes(dep) && !current.includes(dep));
    this.selectedResources.update(list => [...list, r, ...autoAdded]);

    if (autoAdded.length) {
      this.toast.info('Required resources added', `${autoAdded.join(', ')} added automatically — required by ${r}.`);
    }
  }

  // ── drag-to-reorder the Step 3 data-group rows ──────────────────────────────
  // Plain pointer-capture dragging (no CDK, no native HTML5 DnD) — matches the convention already used
  // everywhere else in this feature (canvas cards, tree-node drag, join-popover header drag). Smoothed
  // with a FLIP animation (capture rects before the reorder, let it happen, then animate FROM the old
  // position TO the new one) since a plain DOM/array reorder otherwise just snaps rows into place —
  // and a midpoint threshold so hovering right at a row's edge doesn't flicker the order back and forth.
  readonly draggingResource = signal<string | null>(null);
  private readonly groupsTableBody = viewChild<ElementRef<HTMLElement>>('groupsBody');

  private moveResource(dragged: string, target: string): void {
    if (dragged === target) return;
    this.selectedResources.update(list => {
      const from = list.indexOf(dragged);
      const to = list.indexOf(target);
      if (from === -1 || to === -1) return list;
      const next = [...list];
      next.splice(from, 1);
      next.splice(to, 0, dragged);
      return next;
    });
  }

  /** Runs `reorder`, then slides every displaced row from its pre-reorder position to its new one
   *  (the classic FLIP technique) instead of letting the browser just snap them into place.
   *
   *  Uses afterNextRender() rather than a raw requestAnimationFrame() to read the "after" positions —
   *  a plain rAF scheduled right after a signal update has NO guaranteed ordering against when Angular
   *  actually commits that update to the DOM (this matters most under zoneless change detection, where
   *  there's no zone.js task-boundary flush to piggyback on). Measuring too early reads the still-old
   *  layout, computes a zero delta for every row, skips the animation entirely, and the reorder then
   *  just snaps into place a frame later — which is exactly "not smooth". afterNextRender() is the
   *  API Angular provides specifically to run code only once a render has actually been committed. */
  private animateReorder(reorder: () => void): void {
    const body = this.groupsTableBody()?.nativeElement;
    if (!body) { reorder(); return; }

    const before = new Map<string, DOMRect>();
    body.querySelectorAll<HTMLElement>('[data-resource-row]').forEach(row => {
      before.set(row.dataset['resourceRow']!, row.getBoundingClientRect());
    });

    reorder();

    afterNextRender(() => {
      body.querySelectorAll<HTMLElement>('[data-resource-row]').forEach(row => {
        const oldRect = before.get(row.dataset['resourceRow']!);
        if (!oldRect) return;
        const dy = oldRect.top - row.getBoundingClientRect().top;
        if (!dy) return;
        // Jump back to the old spot with no transition, force a reflow so that's actually painted,
        // then clear the transform on the NEXT frame — the row's own CSS transition animates this
        // last step. (This inner part IS a plain browser paint-cycle concern, not an Angular-render
        // one, so a raw rAF is correct and sufficient here.)
        row.style.transition = 'none';
        row.style.transform = `translateY(${dy}px)`;
        row.getBoundingClientRect();
        requestAnimationFrame(() => {
          row.style.transition = '';
          row.style.transform = '';
        });
      });
    }, { injector: this.injector });
  }

  onGroupRowDragHandlePointerDown(r: string, ev: PointerEvent): void {
    if (ev.button !== 0) return;
    ev.preventDefault();
    (ev.currentTarget as HTMLElement).setPointerCapture(ev.pointerId);
    this.draggingResource.set(r);
  }

  onGroupRowDragPointerMove(ev: PointerEvent): void {
    const dragged = this.draggingResource();
    if (!dragged) return;
    const overRow = (document.elementFromPoint(ev.clientX, ev.clientY) as HTMLElement | null)
      ?.closest<HTMLElement>('[data-resource-row]');
    const overResource = overRow?.dataset['resourceRow'];
    if (!overResource || overResource === dragged) return;

    const list = this.selectedResources();
    const movingDown = list.indexOf(dragged) < list.indexOf(overResource);
    const midpoint = overRow!.getBoundingClientRect().top + overRow!.getBoundingClientRect().height / 2;
    const pastMidpoint = movingDown ? ev.clientY > midpoint : ev.clientY < midpoint;
    if (!pastMidpoint) return;

    this.animateReorder(() => this.moveResource(dragged, overResource));
  }

  onGroupRowDragPointerUp(ev: PointerEvent): void {
    const el = ev.currentTarget as HTMLElement;
    if (el.hasPointerCapture(ev.pointerId)) el.releasePointerCapture(ev.pointerId);
    this.draggingResource.set(null);
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
  readonly dataTypeForTableColumnFn = (tableFullName: string, column: string): string | undefined =>
    this.dataTypeForTableColumn(tableFullName, column);

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
    this.resolvedDestinationId.set(f['destinationId'] || null);
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
    // Preferred: the canonical Mapping JSON — restores tables/relations/mappings in one shot, including
    // anything dest_mappings_v2 alone can't (e.g. which extra tables are children, and of what). Falls
    // back to dest_mappings_v2/dest_mappings for nodes saved before this contract existed.
    if (f['dest_mapping_summary_v1']) {
      try {
        this.loadMappingSummary(JSON.parse(f['dest_mapping_summary_v1']) as MappingSummaryDocument);
        return;
      } catch { /* fall through to the older loaders below */ }
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

  // Connection-only fields (server/database/auth for SQL; folder/sftp for CSV), keyed the same way both
  // _save() and provisionDestinationConnection() need them — shared so the two never drift apart.
  private _buildConnectionConfig(): Record<string, string> {
    const config: Record<string, string> = {};
    if (this.destType() === 'sql') {
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
    return config;
  }

  // Creates (or updates, if Step 1 was already provisioned earlier this session) the real DestinationConfiguration
  // as soon as Step 1's connection details are complete — so a real destinationId exists immediately, the same way
  // sourceConnectionId now does for the Epic source wizard (see wizard.service.ts's save()), rather than only after
  // the whole workflow gets built. Skipped when reusing an existing connection (selectedExistingId already has a
  // real id) or when editing a destination whose connection came from an "existing" pick (same reason).
  private provisionDestinationConnection(onDone: () => void): void {
    if (this.connectionMode() === 'existing') {
      onDone();
      return;
    }

    const config = this._buildConnectionConfig();
    const isSql = this.isSql();
    const name = config['dest_name'] || (isSql ? 'SQL Destination' : 'File Destination');
    const secretName = newSecretName(name);
    const request: CreateDestinationConfigurationRequest = isSql
      ? {
          name,
          destinationType: 'SqlServer',
          keyVaultName: 'workflow-secrets',
          secretName,
          target: null,
          inlineSecret: buildSqlConnectionString(config),
          connectionMetadataJson: buildConnectionMetadata(config, true),
        }
      : {
          name,
          destinationType: config['dest_storageType'] === 'sftp' ? 'Sftp' : 'Csv',
          keyVaultName: 'workflow-secrets',
          secretName,
          target: config['dest_filePattern'] || null,
          inlineSecret: config['dest_storageType'] === 'sftp' ? buildSftpUri(config) : (config['dest_folder'] || ''),
          connectionMetadataJson: buildConnectionMetadata(config, false),
        };

    const existingId = this.resolvedDestinationId();
    this.provisioningDestination.set(true);
    const obs = existingId
      ? this.destinationConfigSvc.update(existingId, request)
      : this.destinationConfigSvc.create(request);
    obs.subscribe({
      next: dto => {
        this.resolvedDestinationId.set(dto.id);
        this.provisioningDestination.set(false);
        onDone();
      },
      error: err => {
        this.provisioningDestination.set(false);
        const msg = err?.error?.title ?? err?.error?.error ?? err?.message ?? 'Failed to create the destination connection.';
        this.toast.show('Destination not created', typeof msg === 'string' ? msg : 'Failed to create the destination connection.');
      },
    });
  }

  private _save(): void {
    const type = this.destType();
    const config: Record<string, string> = {
      dest_resources: this.selectedResources().join(','),
      ...this._buildConnectionConfig(),
    };

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
    } else if (this.connectionMode() === 'new' && this.resolvedDestinationId()) {
      // Step 1 already created/updated the real DestinationConfiguration with these exact connection details
      // (see provisionDestinationConnection) — mark it resolved so workflow-build-assembler.service.ts doesn't
      // redundantly recreate the secret under a new name on every build.
      config['destinationId'] = this.resolvedDestinationId()!;
      config['destinationResolved'] = 'true';
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
    // The canonical Mapping JSON (see field-mapping-summary.model.ts) — additive alongside the two keys
    // above; this is what _populateFromNode prefers on reload, and what "Save mapping"/the export
    // preview modal show. Includes what dest_mappings_v2 alone can't: which extra tables are children
    // and of what (childTableRelationsByTable).
    config['dest_mapping_summary_v1'] = JSON.stringify(buildMappingSummaryDocument({
      sourceVendor: this.sourceVendor().toUpperCase(),
      destType: type,
      destLabel: this.destLabel(),
      mappingRows: this.mappingRows(),
      sqlTables: this.sqlTables(),
      childTableRelationsByTable: this.childTableRelationsByTable(),
      availableFields: this.availableFieldsFn,
      sourceConnectionId: this.sourceConnectionId(),
      destinationId: this.selectedExistingId() ?? this.resolvedDestinationId(),
    }));

    this.saved.emit({
      attachNode:  this.attachNode(),
      transformId: type === 'sql' ? 'dest-sqlserver' : 'dest-csv',
      status:      'enabled',
      config,
    });
  }
}
