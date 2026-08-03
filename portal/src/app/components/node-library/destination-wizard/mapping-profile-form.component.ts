import { Component, input, signal, computed, effect, untracked, inject } from '@angular/core';
import { DestinationColumn, DestinationTable } from '../../../services/destination-schema.service';
import { MappingCatalogService, FhirElement, resolveParentReferenceField } from '../../../services/mapping-catalog.service';

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
  // ── Advanced MappingFieldDto members (docs/backend/14-mapping-profile-master-screen-plan.md §3.2) ──
  // The wizard has no UI to author these — only the Mapping Profiles master screen does — but a saved
  // profile pulled through the wizard must round-trip them losslessly rather than silently dropping them.
  // MaxLength/Precision/Scale are deliberately excluded: MappingFieldDto documents them as never persisted
  // on the profile itself, only filled in at pipeline run time from the destination's live schema.
  isRequired?: boolean;
  defaultValue?: string;
  format?: string;
  normalizationType?: string;
  terminologySystemJsonPath?: string;
  terminologyCodeJsonPath?: string;
  arrayPolicy?: string;
  cardinality?: string;
  correlationCodeJsonPath?: string;
  correlationCodeValue?: string;
  isEnabled?: boolean;
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

/**
 * Field-mapping editor extracted from DestinationWizardComponent's step 3 ("Map fields") — the mapping-profile
 * form component the whole master-screen plan hinges on (docs/backend/14-mapping-profile-master-screen-plan.md
 * §5.1/§10.1: share the field-level form, not the shell). Knows nothing about node config / dest_mappings
 * serialization — that boundary stays in the host (the wizard today; a Settings dialog and the Field Mapping
 * node dialog later), same split as DestinationConnectionFormComponent already draws for the connection form.
 *
 * Deliberately mirrors the wizard's own destType-driven behavior 1:1 rather than introducing the plan's forward-
 * looking `TargetSchemaOwnership` registry — that registry doesn't exist as code anywhere yet (§2 describes it as
 * design for a later slice), and this extraction's job is "no behavior change," not building it early.
 */
@Component({
  selector: 'app-mapping-profile-form',
  standalone: true,
  imports: [],
  templateUrl: './mapping-profile-form.component.html',
  styleUrls: ['./destination-wizard.component.scss'],
})
export class MappingProfileFormComponent {
  private readonly catalogSvc = inject(MappingCatalogService);

  readonly resources = input.required<string[]>();
  readonly destType   = input.required<'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres'>();
  /** Live probed SQL table/column metadata (empty for CSV/Mongo, or before a SQL probe succeeds). */
  readonly sqlTables  = input<DestinationTable[]>([]);
  readonly initialRows              = input<MappingRow[]>([]);
  readonly initialTargets           = input<Record<string, string>>({});
  readonly initialParentSelections  = input<Record<string, string[]>>({});
  readonly readOnly = input(false);

  // Backend FHIR catalog fields per resource type (array-aware paths). Empty until fetched; the
  // built-in DEST_RESOURCE_DEFS act as the fallback when a resource isn't (yet) loaded.
  private readonly catalogByResource = signal<Record<string, ResourceFieldDef[]>>({});

  // ── mapping rows ──────────────────────────────────────────────────────────
  readonly mappingRows = signal<MappingRow[]>([]);

  // Per-resource destination target (CSV file name / SQL table), entered once in the group header
  // rather than repeated on every mapping row.
  readonly targetByResource = signal<Record<string, string>>({});

  // Which other selected resources each resource is configured as a "child" of — e.g.
  // { Observation: ['Patient', 'Encounter'] } means Observation independently requires a mapped
  // reference field to both. A resource can have several parents at once (see _reconcileParentRefRows).
  readonly parentSelections = signal<Record<string, string[]>>({});

  readonly isSql   = computed(() => this.destType() === 'sql' || this.destType() === 'mysql' || this.destType() === 'postgres');
  readonly isMongo = computed(() => this.destType() === 'mongo');

  /** Resource field/target definition — the built-in catalog entry, or a generic fallback for any other resource. */
  private defFor(r: string): ResourceDef {
    return DEST_RESOURCE_DEFS[r] ?? genericResourceDef(r);
  }

  constructor() {
    // Seed initial state exactly once per input change (load-and-edit / a saved node) — mirrors the wizard's own
    // _populateFromNode timing (ngOnInit, before any reconciliation effect runs).
    effect(() => {
      const rows = this.initialRows();
      const targets = this.initialTargets();
      const parents = this.initialParentSelections();
      untracked(() => {
        if (rows.length) this.mappingRows.set(rows);
        if (Object.keys(targets).length) this.targetByResource.set(targets);
        if (Object.keys(parents).length) this.parentSelections.set(parents);
      });
    });

    // Rebuild mapping rows whenever selected resources or destType change
    effect(() => {
      const resources = this.resources();
      const type      = this.destType();
      untracked(() => this._rebuildRows(resources, type));
    });

    // Fetch the array-aware FHIR catalog for every data group on offer. The field picker prefers it
    // over the built-in fallback once loaded. Deduped via _requested so this effect never re-fetches.
    effect(() => {
      for (const r of this.resources()) this._ensureCatalog(r);
    });

    // Keeps each selected resource's mandatory id/upsert-key row in sync — inserted the moment a resource is
    // selected, upgraded to the schema-verified PK/unique column once the destination table's columns load,
    // and refreshed if the catalog's id field metadata changes. See _reconcileIdRows for the merge rules.
    effect(() => {
      this.resources();
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
      this.resources();
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

  hasSqlTables(): boolean {
    return this.isSql() && this.sqlTables().length > 0;
  }

  sqlTableOptions(): string[] {
    return this.sqlTables().map(t => t.fullName);
  }

  // Table currently chosen for a resource, with full column metadata (name/type/PK/unique).
  private tableForResourceTarget(r: string): DestinationTable | undefined {
    const target = this.targetFor(r);
    return this.sqlTables().find(t => t.fullName === target || t.tableName === target);
  }

  // Columns of the table currently chosen for a resource (drives the per-row column dropdown). Identity/
  // auto-generated columns (e.g. an IDENTITY primary key) are omitted — the database populates them, so
  // mapping a field onto one produces a write that SQL Server rejects ("Cannot insert explicit value for
  // identity column").
  columnsForResourceTarget(r: string): string[] {
    return (this.tableForResourceTarget(r)?.columns ?? [])
      .filter(c => !c.isAutoGenerated)
      .map(c => c.name);
  }

  // True when a resource's live target table loaded real columns but every one of them is identity/computed
  // (columnsForResourceTarget is therefore empty) — distinguishes that case from "no schema loaded yet" so the
  // template can show an explanatory message instead of silently falling back to a free-text column input,
  // which otherwise looks identical to the CSV/unprobed case and leaves the admin guessing why no columns show.
  allColumnsAutoGenerated(r: string): boolean {
    const columns = this.tableForResourceTarget(r)?.columns ?? [];
    return columns.length > 0 && columns.every(c => c.isAutoGenerated);
  }

  // ── parent-child reference mapping ───────────────────────────────────────
  // Other selected resources that `r` could be a child of — i.e. `r` has at least one FHIR reference
  // field whose allowed target types include that resource. Only these are offered as parent choices,
  // so the UI can never be pushed into a pairing FHIR doesn't actually support.
  candidateParentsFor(r: string): string[] {
    const fields = this.availableFields(r);
    return this.resources().filter(other =>
      other !== r && resolveParentReferenceField(this._asFhirElements(fields), other) !== null);
  }

  selectedParentsOf(r: string): string[] {
    return this.parentSelections()[r] ?? [];
  }

  isParentSelected(r: string, parent: string): boolean {
    return this.selectedParentsOf(r).includes(parent);
  }

  toggleParent(r: string, parent: string): void {
    if (this.readOnly()) return;
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

  // ── Map fields accordion ─────────────────────────────────────────────────
  // Explicit per-resource overrides — only touched by toggleGroup(). Whichever set contains a resource wins;
  // if neither does, isGroupCollapsed() falls back to a sensible default (see below) rather than requiring
  // every resource to be explicitly toggled once before it renders correctly.
  private readonly _collapsedGroups = signal<Set<string>>(new Set());
  private readonly _expandedGroups  = signal<Set<string>>(new Set());

  // Default (no explicit toggle yet): a single resource always starts expanded — there's nothing to declutter.
  // With several resources, only the first stays open and the rest start collapsed; a resource with an active
  // mapping issue defaults open too, so a problem is never hidden behind a fold the user never opened.
  isGroupCollapsed(r: string): boolean {
    if (this._collapsedGroups().has(r)) return true;
    if (this._expandedGroups().has(r)) return false;
    if (this.resources().length <= 1) return false;
    if (this.resourceIssueCount(r) > 0) return false;
    return this.resources().indexOf(r) > 0;
  }

  toggleGroup(r: string): void {
    const collapsedNow = this.isGroupCollapsed(r);
    if (collapsedNow) {
      this._expandedGroups.update(s => new Set(s).add(r));
      this._collapsedGroups.update(s => { const n = new Set(s); n.delete(r); return n; });
    } else {
      this._collapsedGroups.update(s => new Set(s).add(r));
      this._expandedGroups.update(s => { const n = new Set(s); n.delete(r); return n; });
    }
  }

  // Badge shown on the group header regardless of collapse state, so collapsing a resource's mapping table
  // never hides an unverified column, a type mismatch, or a missing required parent selection from view.
  resourceIssueCount(r: string): number {
    let count = this.rowsForResource(r)
      .filter(row => this.isRowColumnUnverified(row) || this.isRowTypeMismatched(row)).length;
    if (this.resourcesMissingParentSelection().includes(r)) count++;
    return count;
  }

  updateRow(i: number, field: 'targetName', val: string): void {
    if (this.readOnly()) return;
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
    if (this.readOnly()) return;
    if (this.isSql() && !this.targetFor(resource)) return; // no table chosen yet — nothing to map columns against
    const fields = this.availableFields(resource);
    if (!fields.length) return;
    const used = new Set(this.rowsForResource(resource).map(r => r.fieldLabel));
    const next = fields.find(f => !used.has(f.label)) ?? fields[0];
    const row: MappingRow = {
      resource,
      fieldLabel: next.label,
      fhirPath:   next.path,
      targetName: this.isSql() ? next.sqlColumn : next.csvColumn,
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
    if (this.readOnly()) return;
    if (this.isSql() && !this.targetFor(resource)) return; // no table chosen yet — nothing to map columns against
    const idField = this.idFieldFor(resource);
    const used = new Set(this.rowsForResource(resource).map(r => r.fieldLabel));
    const remaining = this.availableFields(resource).filter(f =>
      !used.has(f.label) && f.path !== idField?.path);
    if (!remaining.length) return;

    const table = this.tableForResourceTarget(resource);
    // Never auto-match a field onto an identity/auto-generated column — the database owns its value.
    const columns = (table?.columns ?? []).filter(c => !c.isAutoGenerated);
    const usedColumns = new Set(this.rowsForResource(resource).map(r => r.targetName).filter(Boolean));

    const newRows: MappingRow[] = [];

    if (!columns.length) {
      // No live schema loaded (CSV, or SQL not yet probed) — fall back to the built-in naming convention,
      // same guess addRow() uses for a single field.
      for (const f of remaining) {
        newRows.push(this._buildRow(resource, f, this.isSql() ? f.sqlColumn : f.csvColumn));
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
        if (cand.score < MappingProfileFormComponent.AUTO_MATCH_THRESHOLD) continue;
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
    const suggested = this.isSql() ? field.sqlColumn : field.csvColumn;
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
    if (this.readOnly()) return;
    if (this.isIdRow(row)) return; // mandatory — guarantees every resource keeps its upsert key mapped
    if (row.isRequiredParentRef) return; // mandatory while its parent chip is selected — toggle the chip instead
    this.mappingRows.update(rows => {
      const i = rows.indexOf(row);
      if (i < 0) return rows;
      return [...rows.slice(0, i), ...rows.slice(i + 1)];
    });
  }

  // Clears every removable row for a resource in one click (mirrors removeRow's own exemptions — the id row and
  // any locked parent-reference rows stay, since both are mandatory and neither has a manual remove control).
  clearFields(resource: string): void {
    if (this.readOnly()) return;
    this.mappingRows.update(rows =>
      rows.filter(row => row.resource !== resource || this.isIdRow(row) || !!row.isRequiredParentRef));
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
  // Identity/auto-generated columns are ignored: a table whose only key is an IDENTITY primary key must NOT
  // have that column auto-locked as the upsert target (the write would fail), so the id row falls back to a
  // live picker of the real, writable columns instead.
  private _autoMatchIdColumn(r: string): string | null {
    const columns = this.tableForResourceTarget(r)?.columns ?? [];
    return columns.find(c => c.isPrimaryKey && !c.isAutoGenerated)?.name
      ?? columns.find(c => c.isUnique && !c.isAutoGenerated)?.name
      ?? null;
  }

  // True when the named column of a resource's live target table is an identity/auto-generated column — used to
  // heal saved rows that still point at one (see _reconcileIdRows). Unknown columns (no live schema) are not
  // flagged, so nothing is cleared when the schema hasn't loaded.
  private _isAutoGeneratedColumn(r: string, columnName: string): boolean {
    if (!columnName) return false;
    return (this.tableForResourceTarget(r)?.columns ?? [])
      .some(c => c.name === columnName && !!c.isAutoGenerated);
  }

  // True once the destination column for a resource's id row is schema-verified (a real PK/unique column was
  // found) — only then is it safe to fully lock the field, since a guessed name could otherwise be wrong.
  isIdColumnLocked(r: string): boolean {
    return this._autoMatchIdColumn(r) !== null;
  }

  // True when this resource's live column list is known (a schema probe succeeded) but the id row's mapped
  // column isn't actually one of those real columns — e.g. still left at the wizard's unverified default
  // guess, or a stale value from before the table was reselected. Saving in this state is exactly what
  // produces "Invalid column name 'X'" at run time, since the column genuinely doesn't exist on the
  // customer's table. Returns false (nothing to flag) when the schema isn't known yet — there's no live
  // column list to check the id row against.
  isIdColumnUnverified(r: string): boolean {
    if (this.isIdColumnLocked(r)) return false;
    const columns = this.columnsForResourceTarget(r);
    if (columns.length === 0) return false;
    const idRow = this.mappingRows().find(row => row.resource === r && this.isIdRow(row));
    return !idRow || !columns.includes(idRow.targetName);
  }

  // Same check as isIdColumnUnverified but for any mapped row, not just the id row — a non-id field left at a
  // stale/guessed column name (e.g. after switching tables) fails the write with "Invalid column name" exactly
  // the same way the id row does, just on a column that isn't the upsert key. The id row is schema-verified
  // separately (isIdColumnLocked) when a PK/unique was auto-matched, so it's excluded here to avoid flagging a
  // row the user was never shown an editable picker for in the first place.
  isRowColumnUnverified(row: MappingRow): boolean {
    if (this.isIdRow(row) && this.isIdColumnLocked(row.resource)) return false;
    const columns = this.columnsForResourceTarget(row.resource);
    if (columns.length === 0) return false;
    return !columns.includes(row.targetName);
  }

  // The live target column a row is currently mapped to, or undefined when the schema isn't loaded / the
  // column isn't a real one on this table (see isRowColumnUnverified — that case is reported separately).
  private _targetColumn(row: MappingRow): DestinationColumn | undefined {
    return this.tableForResourceTarget(row.resource)?.columns.find(c => c.name === row.targetName);
  }

  // Mirrors the server-side check in CreateMappingProfileRequestValidator: a field's FHIR value type
  // (String/Integer/Decimal/Boolean/Date/DateTime/Json) must match the destination column's mapping value
  // type, or the write engine can't coerce one to the other. Today this is caught only when the save request
  // hits the backend validator — surfacing it here lets the admin fix it during mapping instead of after a
  // rejected save. Only flagged once the column itself is schema-verified (isRowColumnUnverified false) and
  // the row actually carries catalog-derived valueType metadata (absent for the built-in fallback defs).
  isRowTypeMismatched(row: MappingRow): boolean {
    if (!row.valueType) return false;
    if (this.isRowColumnUnverified(row)) return false;
    const column = this._targetColumn(row);
    if (!column) return false;
    return !column.mappingValueType || column.mappingValueType.toLowerCase() !== row.valueType.toLowerCase();
  }

  // Human-readable message for isRowTypeMismatched, phrased the same way as the server-side validator's
  // rejection so the admin sees identical wording whether the mismatch is caught here or (for anything this
  // client-side check misses) at save time.
  typeMismatchMessage(row: MappingRow): string {
    const column = this._targetColumn(row);
    if (!column) return '';
    return `'${column.name}' is a ${column.dataType} column (expects ${column.mappingValueType}), but this field is mapped as ${row.valueType}.`;
  }

  // Blocks proceeding past the mapping step while any selected resource has a mapped row (id or otherwise)
  // whose column isn't verified against the live destination schema — the save-time gate the destination-node
  // config alone can't guarantee, since nothing upstream forces the user to actually pick from the live column
  // list rather than leaving an unmatched guess in place.
  hasUnverifiedColumns(): boolean {
    return this.mappingRows().some(row =>
      this.resources().includes(row.resource) && this.isRowColumnUnverified(row));
  }

  // Same gate as hasUnverifiedColumns, for type mismatches (see isRowTypeMismatched) — blocks proceeding
  // past the mapping step so a save-time rejection from CreateMappingProfileRequestValidator is never the
  // first the admin hears of it.
  hasTypeMismatchedColumns(): boolean {
    return this.mappingRows().some(row =>
      this.resources().includes(row.resource) && this.isRowTypeMismatched(row));
  }

  // Resources offering at least one candidate parent (per candidateParentsFor) with none picked yet — an
  // unselected chip means _reconcileParentRefRows never locks in that reference field, so the row saves with no
  // link back to its actual parent. Blocks Next/Save until at least one parent is chosen per such resource
  // (the host gates on this) rather than only warning after the fact.
  resourcesMissingParentSelection(): string[] {
    return this.resources().filter(r =>
      this.candidateParentsFor(r).length > 0 && this.selectedParentsOf(r).length === 0);
  }

  // Keeps every selected resource's mandatory id row in sync with the live schema and the catalog: inserts it
  // if missing, forces isUpsertKey true, and upgrades its destination column to the schema-verified PK/unique
  // column once one is found (never overwrites a column that isn't schema-verified, so a value loaded from a
  // saved node — or typed in before the schema had a detectable key — is never silently clobbered by a guess).
  private _reconcileIdRows(): void {
    let changed = false;
    let next = [...this.mappingRows()];

    for (const r of this.resources()) {
      // SQL destinations pick a target table per resource (the group header's "Select a table…" dropdown) —
      // nothing should populate until the admin has actually chosen one, otherwise the mandatory id row (and
      // any rows added via "+ Add field" / "Add Fields") shows up against no real table at all. CSV/Mongo have
      // no comparable "not yet chosen" state (targetFor always holds a usable default), so they're unaffected.
      if (this.isSql() && !this.targetFor(r)) continue;

      const idField = this.idFieldFor(r);
      if (!idField) continue;

      const autoColumn = this._autoMatchIdColumn(r);
      const idx = next.findIndex(row => row.resource === r && this.isIdRow(row));

      if (idx === -1) {
        const initialColumn = autoColumn ?? (this.isSql() ? idField.sqlColumn : idField.csvColumn);
        next = [{ ...this._buildRow(r, idField, initialColumn), isUpsertKey: true }, ...next];
        changed = true;
      } else {
        const row = next[idx];
        // Heal a saved row that targets an identity/auto-generated column (e.g. an older workflow that locked
        // the id onto an IDENTITY primary key): clear it so the id row falls back to a live picker of writable
        // columns, rather than silently keeping a target the write can never satisfy.
        const savedIsAutoGenerated = this._isAutoGeneratedColumn(r, row.targetName);
        const desiredColumn = autoColumn ?? (savedIsAutoGenerated ? '' : row.targetName);
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
    const selected = new Set(this.resources());
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

    for (const r of this.resources()) {
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
            ? (this.isSql() ? matchingFieldDef.sqlColumn : matchingFieldDef.csvColumn)
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
    if (this.readOnly()) return;
    this.targetByResource.update(m => ({ ...m, [r]: val }));
  }

  // ── business-field selection ───────────────────────────────────────────────
  availableFields(r: string): ResourceFieldDef[] {
    // Prefer the array-aware backend catalog; fall back to the built-in defs until it loads (or if offline).
    return this.catalogByResource()[r] ?? this.defFor(r).fields;
  }

  changeBusinessField(i: number, label: string): void {
    if (this.readOnly()) return;
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
        targetName: f ? (this.isSql() ? f.sqlColumn : f.csvColumn) : row.targetName,
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
  private _rebuildRows(resources: string[], type: 'sql' | 'csv' | 'mysql' | 'mongo' | 'postgres'): void {
    const targets = { ...this.targetByResource() };
    for (const r of resources) {
      if (targets[r]) continue;
      // SQL: leave unset so the table dropdown genuinely shows its "Select a table…" placeholder and
      // requires an explicit pick — a hardcoded guess here (e.g. dbo.Patient) rarely matches the
      // customer's real table name (e.g. dbo.Patient_New), and once it doesn't match any <option>, the
      // native <select> silently falls back to displaying its first listed table — alphabetically
      // whatever that happens to be, with no relation to the resource — which reads as an intentional,
      // correct selection the user never actually made. CSV has no such mismatch risk (it's a free-text
      // filename input, not a dropdown of real destination objects), so keep suggesting one there.
      if (type === 'csv') {
        targets[r] = this.defFor(r).csvFile;
      }
    }
    this.targetByResource.set(targets);
    this.mappingRows.update(rows =>
      rows
        .filter(row => resources.includes(row.resource))
        .map(row => ({ ...row, tableName: targets[row.resource] ?? row.tableName }))
    );
  }

  /** null ⇒ nothing to report; the host reads mappingRows()/targetByResource()/parentSelections() directly for
   *  the actual values — this mirrors DestinationConnectionFormComponent.getConfig()'s "null means invalid"
   *  contract for symmetry, but this form has no single point of invalidity beyond the gates already exposed
   *  (hasUnverifiedColumns/hasTypeMismatchedColumns/resourcesMissingParentSelection). */
  getRows(): MappingRow[] {
    return this.mappingRows();
  }
}
