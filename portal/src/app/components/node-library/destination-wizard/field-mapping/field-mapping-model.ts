// ── Field-mapping data model ─────────────────────────────────────────────────
// Extends the wizard's original single-source MappingRow with joins (multiple
// source fields into one column) and array "instance selection" (all/first/
// nth/criteria), while keeping the legacy flat dest_mappings wire format alive
// so workflow-build-assembler.service.ts keeps working unmodified.

import {
  CreateTableRequest, AddColumnRequest, DropColumnRequest, AlterColumnRequest, DestinationTable,
} from '../../../../services/destination-schema.service';

/**
 * A schema-authoring action (create table / add column / drop column / alter column) the user has
 * triggered on the mapping canvas, queued instead of executed immediately — the real DDL only runs
 * once "Add to Pipeline" flushes the queue (see DestinationWizardComponent.flushPendingSchemaOps), so
 * nothing touches the live database while the user is still just exploring/mapping. Each request object
 * is already the exact shape DestinationSchemaService's corresponding method expects.
 */
export type PendingSchemaOp =
  | { kind: 'createTable'; request: CreateTableRequest }
  | { kind: 'addColumn'; request: AddColumnRequest }
  | { kind: 'dropColumn'; request: DropColumnRequest }
  | { kind: 'alterColumn'; request: AlterColumnRequest };

/** The wizard's destination family — SQL Server/MySQL/PostgreSQL share the relational (sqlTables/columns)
 *  path, Mongo and CSV don't. Kept as one union (rather than a plain boolean) so summary/snapshot code can
 *  still tell the SQL engines apart where it matters (e.g. destination.type on the wire). */
export type MappingDestType = 'sql' | 'csv' | 'mysql' | 'postgres' | 'mongo' | 'medplum' | 'fhir' | 'blob';

/** True for every destType with a live, relational table/column schema — SQL Server, MySQL, PostgreSQL —
 *  the "SQL family" that shares table creation/column ALTER/probe semantics, as opposed to CSV (flat
 *  file), Mongo (schemaless collections), or the FHIR-native destinations. DestinationWizardComponent and
 *  MappingProfileFormComponent each already have their own local `isSql` computed with this exact same
 *  union (destType() === 'sql' || 'mysql' || 'postgres') since they hold a live destType() signal to
 *  compute it from; this plain-function twin exists for the leaf field-mapping components (target-card,
 *  canvas, preview-drawer) that only ever receive a MappingDestType value as an input, not a wizard
 *  instance to call .isSql() on. Kept in one place so "is this destType SQL-like" can never drift into a
 *  bare `=== 'sql'` literal that silently excludes MySQL/PostgreSQL again (see the Map Fields "MySQL shows
 *  as CSV" bug this fixed — target-card's kind/status-badge/add-column branches and the output preview
 *  drawer's format/title all used to check `=== 'sql'` directly). */
export function isSqlFamilyDestType(destType: MappingDestType): boolean {
  return destType === 'sql' || destType === 'mysql' || destType === 'postgres';
}

export interface MappingSourceRef {
  fhirPath: string;
  label: string;
  jsonPath?: string;
  valueType?: string;
  arrays?: string[];
}

export interface MappingInstanceSelection {
  type: 'all' | 'first' | 'nth' | 'criteria';
  n?: number;
  field?: string;
  op?: '=' | '!=' | 'contains';
  value?: string;
  /** Only meaningful when type === 'all'. */
  aggregate?: 'rows' | 'csv';
}

export interface MappingRow {
  resource: string;
  /** Ordered; length 1 = simple map, length > 1 = join. Empty when mode === 'childJson'. */
  sources: MappingSourceRef[];
  mode: 'value' | 'childJson';
  /** The group's full FmTreeNode id (e.g. "Patient.name"), only when mode === 'childJson'. */
  childNodeId?: string;
  /** Join delimiter; only meaningful when sources.length > 1. */
  delimiter?: string;
  /** Absent is treated as {type:'first'} — see resolveArrayPolicy / migrateLegacyRow. */
  instance?: MappingInstanceSelection;
  targetName: string;
  tableName: string;
  /** No UI control sets these yet (the wire format hardcodes false/null/null) — present only so a
   *  MappingSnapshot round-trips the full backend field shape once a control exists. */
  isRequired?: boolean;
  defaultValue?: string | null;
  format?: string | null;
  /**
   * Set when this field is a FHIR reference (its primary source's fhirPath ends in ".reference", e.g.
   * "Observation.subject.reference") that should be resolved against another mapped RESOURCE's own
   * table + id column at write time, instead of being written verbatim — a bigint FK column can never
   * accept the raw "Patient/xyz" string. Value is the OTHER resource type (e.g. "Patient"); its actual
   * target table and own "$.id"-mapped column name are derived automatically once every resource's
   * mapping is known (see resolveReferenceLookup in field-mapping-summary.model.ts) — this keeps the
   * control a simple "which resource does this point to?" picker rather than asking the user to know
   * physical table/column names.
   */
  referencesResource?: string | null;
  /**
   * User-designated upsert key for this row's resource, set via the target card's key toggle — takes
   * full precedence over the destination table's real PK column once ANY row for the resource has this
   * set true (see serializeRowsFlat). Lets a user key an upsert off a natural/business column instead of
   * being forced onto whichever column the schema happens to flag as the physical primary key.
   */
  isUpsertKey?: boolean;
}

/** The legacy flat shape already round-tripped through node.fields['dest_mappings']. */
export interface LegacyMappingRow {
  resource: string;
  field: string;
  path: string;
  target: string;
  column: string;
  jsonPath?: string;
  valueType?: string;
  arrays?: string[];
}

/**
 * Upgrades an old single-source row. Defaults instance to {type:'first'} — NOT 'all' — because
 * workflow-build-assembler.service.ts's buildMapping() already sends arrayPolicy: 'FirstItem' for
 * every array field today; defaulting to 'all' here would silently change already-provisioned
 * pipelines from "first item" to "repeat as rows" the next time the node is re-saved.
 */
export function migrateLegacyRow(row: LegacyMappingRow): MappingRow {
  return {
    resource: row.resource,
    sources: [{
      fhirPath: row.path,
      label: row.field,
      jsonPath: row.jsonPath,
      valueType: row.valueType,
      arrays: row.arrays,
    }],
    mode: 'value',
    delimiter: undefined,
    instance: { type: 'first' },
    targetName: row.column,
    tableName: row.target,
  };
}

/** Structurally identical to field-mapping-summary.model.ts's ChildTableRelation — redeclared here (rather
 *  than imported) to avoid a circular import, since that file already imports from this one. */
export interface FlatChildTableRelation {
  parentTable: string;
  parentColumn: string;
  foreignKeyColumnName: string;
}

/**
 * Flattens the rich MappingRow[] back to the legacy wire shape for dest_mappings (see serializeRowsFlat).
 * `sqlTables`, when supplied, is used to tag each row `isUpsertKey: true` when its target column is the
 * destination table's real primary key — see serializeRowsFlat's own doc comment for why. `childTableRelationsByTable`,
 * when supplied, lets a row mapped onto a table OTHER than its resource's own primary/root table (e.g. an
 * array fanned out into a separate child table) carry the parent/foreign-key linkage workflow-build-assembler
 * .service.ts needs to route that field to its own table at write time — see MappingField.ParentTable/
 * ParentKeyColumn/ForeignKeyColumn on the backend, which this mirrors exactly (MappingImportService.BuildFieldAsync
 * populates them the same way for the "Save mapping" / mapping-profiles/import path).
 */
export function serializeRowsFlat(
  rows: MappingRow[],
  targetByResource: Record<string, string>,
  sqlTables: DestinationTable[] = [],
  childTableRelationsByTable: Record<string, FlatChildTableRelation> = {},
): (LegacyMappingRow & {
  arrayPolicy: string; approximated: boolean; isUpsertKey: boolean;
  parentTable?: string; parentKeyColumn?: string; foreignKeyColumn?: string;
  referencesResource?: string;
})[] {
  // Once the user has explicitly marked ANY row for a resource as the upsert key (via the target card's
  // key toggle), that choice is authoritative for the whole resource — the real PK column is no longer
  // consulted at all, so a business/natural key can be used even when it isn't the table's physical PK.
  const resourcesWithExplicitKey = new Set(rows.filter(r => r.isUpsertKey === true).map(r => r.resource));
  return rows.map(row => {
    const primary = row.sources[0];
    // A row's OWN table is always the real destination for its column — critically, this must NOT fall
    // back to the resource's primary table when they differ (see rootTable below), or a field mapped onto
    // a genuine child/extra table (e.g. "Use" on dbo.PatientName) gets silently validated/written against
    // the wrong table's columns (dbo.Patient), which doesn't have that column at all.
    const targetTableName = row.tableName;
    const rootTable = targetByResource[row.resource];
    const isChildTable = !!rootTable && targetTableName !== rootTable;
    // A genuine child-table relation only counts when its declared parent is actually this resource's own
    // root table — the same cross-resource guard field-mapping-summary.model.ts's resolveTable applies, so
    // an unrelated live-schema FK (e.g. Encounter.PatientId -> Patient.Id, a cross-resource reference, not
    // a same-resource child table) never gets mistaken for one here.
    const relation = isChildTable ? childTableRelationsByTable[targetTableName] : undefined;
    const genuineRelation = relation && relation.parentTable === rootTable ? relation : undefined;

    const resolvedPolicy = resolveArrayPolicy(row);
    const { approximated } = resolvedPolicy;
    // The instance selection (first/all/nth/criteria) is only a meaningful choice on the resource's OWN
    // root table. Off it, there's no such thing as "first item only" — the whole reason a field targets a
    // separate child table is to become its own row there, so it must always be SeparateDestination
    // regardless of which instance type was picked (mirroring MappingImportService.ResolveArrayMetadata's
    // identical isRootTable branch). Only StoreJson is exempt: a childJson row embeds the whole node as one
    // JSON blob rather than fanning out into per-item rows, so it keeps its own resolved policy untouched.
    const arrayPolicy: string = (isChildTable && resolvedPolicy.arrayPolicy !== 'StoreJson')
      ? 'SeparateDestination'
      : resolvedPolicy.arrayPolicy;

    // Without an explicit designation, fall back to whichever mapped field lands on the destination
    // table's REAL primary key column (e.g. PatientId, not necessarily a column named "Id") — not
    // literally whichever field maps the FHIR resource's own ".id" element. workflow-build-assembler
    // .service.ts's buildMappingForResource still also matches jsonPath === '$.id' as a further fallback
    // for rows saved before this existed, or when sqlTables isn't available to check against.
    const targetTable = sqlTables.find(t => t.fullName === targetTableName);
    const isUpsertKey = resourcesWithExplicitKey.has(row.resource)
      ? row.isUpsertKey === true
      : !!targetTable?.columns.some(c => c.name === row.targetName && c.isPrimaryKey);
    return {
      resource: row.resource,
      field: primary?.label ?? '',
      path: primary?.fhirPath ?? row.childNodeId ?? '',
      target: targetTableName,
      column: row.targetName,
      jsonPath: primary?.jsonPath,
      valueType: arrayPolicy === 'StoreJson' ? 'Json' : primary?.valueType,
      arrays: primary?.arrays,
      arrayPolicy,
      approximated,
      isUpsertKey,
      ...(genuineRelation ? {
        parentTable: genuineRelation.parentTable,
        parentKeyColumn: genuineRelation.parentColumn,
        foreignKeyColumn: genuineRelation.foreignKeyColumnName,
      } : {}),
      // Round-tripped through to workflow-build-assembler.service.ts, which resolves it into the referenced
      // resource's own table/id column (MappingFieldRequest.referenceLookupTable/referenceLookupKeyColumn) —
      // without this, the field-mapping-list "which resource does this reference?" picker has no effect at
      // all on what actually gets saved, and a FHIR reference column keeps writing raw "Patient/xyz" strings
      // (or, worse, NULL into a NOT NULL FK column) forever, no matter what the user picks in that dropdown.
      ...(row.referencesResource ? { referencesResource: row.referencesResource } : {}),
    };
  });
}

export interface ArrayPolicyResolution {
  arrayPolicy: 'Scalar' | 'FirstItem' | 'RepeatParent' | 'StoreJson';
  /** True when this row's configuration has no exact backend equivalent and was approximated. */
  approximated: boolean;
}

/**
 * Single source of truth for translating a MappingRow's join/instance-selection UI state into the
 * backend's ArrayPolicy enum (Scalar | FirstItem | RepeatParent | SeparateDestination | StoreJson |
 * RejectIfMultiple — only the first four have a UI entry point in this feature; the remaining two are
 * a future extension). See field-mapping-model.spec.ts for the decision table this implements.
 */
export function resolveArrayPolicy(row: MappingRow): ArrayPolicyResolution {
  if (row.mode === 'childJson') {
    return { arrayPolicy: 'StoreJson', approximated: false };
  }

  const isJoin = row.sources.length > 1;
  const primary = row.sources[0];
  const hasArrayAncestors = !!primary?.arrays?.length;
  const instance = row.instance ?? { type: 'first' };

  if (isJoin) {
    // No backend representation for joining multiple distinct fields — best-effort primary source.
    return { ...applyInstance(instance, hasArrayAncestors), approximated: true };
  }

  return applyInstance(instance, hasArrayAncestors);
}

function applyInstance(
  instance: MappingInstanceSelection,
  hasArrayAncestors: boolean,
): ArrayPolicyResolution {
  switch (instance.type) {
    case 'first':
      return { arrayPolicy: 'FirstItem', approximated: false };
    case 'all':
      if (!hasArrayAncestors) return { arrayPolicy: 'Scalar', approximated: false };
      if (instance.aggregate === 'csv') {
        // No backend concept of "join array items with a delimiter into one column".
        return { arrayPolicy: 'FirstItem', approximated: true };
      }
      return { arrayPolicy: 'RepeatParent', approximated: false };
    case 'nth':
    case 'criteria':
      // Closest existing enum value; wrong whenever n > 0 or the criteria wouldn't pick the first item.
      return { arrayPolicy: 'FirstItem', approximated: true };
  }
}

export function isApproximated(row: MappingRow): boolean {
  return resolveArrayPolicy(row).approximated;
}

/** True whenever this row could plausibly be designated as pointing at another mapped resource's table via
 *  "referencesResource" — any single-value mapped field, not just one whose path happens to end in
 *  ".reference". Not every source feed shapes its parent-id field as a FHIR Reference (e.g. some flatten
 *  straight to a bare "patientId": "abc123" with no "Patient/" prefix) — ExtractReferenceId on the backend
 *  already tolerates that (a value with no "/" is used as-is), so the UI shouldn't be pickier than the
 *  engine actually is. childJson/array rows are excluded: their value is always written as JSON text (see
 *  resolveArrayPolicy's StoreJson branch), a distinct concern from a scalar field pointing at another row. */
export function isReferenceCandidate(row: MappingRow): boolean {
  return row.mode === 'value' && !!row.sources[0]?.fhirPath;
}
