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
export type MappingDestType =
  | 'sql' | 'csv' | 'mysql' | 'postgres' | 'mongo' | 'medplum' | 'fhir' | 'blob'
  // Lake destinations. Both map fields like CSV/Blob do — no live schema, no table qualification — so
  // they deliberately stay OUT of SQL_FAMILY_TYPES below, which makes isSqlFamilyDestType/
  // qualifyTableName/splitTableName/reconcileTargetsForDestTypeSwitch all no-op for them exactly as
  // they already do for 'csv' and 'blob'.
  | 'datalake' | 'fabric'
  // Microsoft Fabric WAREHOUSE — unlike every other type on this line, this one IS relational: a Fabric
  // Warehouse is a SQL Server over TDS with a real, probeable table/column schema, so it joins
  // SQL_FAMILY_TYPES below and gets the live table picker and schema authoring the SQL engines get.
  // 'fabric' above remains the FILE surface (OneLake Files) and stays out, exactly as before.
  | 'fabricwarehouse'
  // Same "no live schema, no table qualification" category as datalake/fabric above.
  | 'apiendpoint';

/** Outcome of the live destination-schema read that populates the SQL table list. Distinguishes the three
 *  states an empty table list can mean, which an empty array alone cannot: never attempted ('idle'), in
 *  flight ('loading'), read successfully — so an empty list really does mean an empty database ('loaded'),
 *  the read was attempted and failed ('failed'), or it could not be attempted at all ('unavailable', e.g.
 *  a reopened node carrying neither a destinationId nor a password, since secrets are stripped on persist).
 *  Without this, 'failed'/'unavailable' render identically to a genuinely empty database, silently hiding
 *  every existing table behind "+ Create a new table…". */
export type SchemaLoadState = 'idle' | 'loading' | 'loaded' | 'failed' | 'unavailable';

// ── SQL-family table-name qualification — the ONE place a bare table name becomes schema-qualified (or
// vice versa) for SQL Server/MySQL/PostgreSQL. Mirrors SqlDestinationSchemaService.SplitTableName's own
// per-dialect default exactly (src/FHIRBridge.Infrastructure/Destinations/SqlDestinationSchemaService.cs),
// so a name derived here and one the backend independently derives from the same bare name always agree.
// Every call site that used to hand-roll its own "dbo."/"public." ternary (destination-wizard.component.ts's
// _qualifyDefaultTable, field-mapping-canvas.component.ts's submitCreateTable/_finishCreateTable,
// field-mapping-summary.model.ts's old private qualify()) goes through these instead. ──────────────────

const SQL_FAMILY_TYPES: readonly MappingDestType[] = ['sql', 'mysql', 'postgres', 'fabricwarehouse'];

/** Whether `type` is one of the three relational engines table qualification applies to at all. */
export function isSqlFamilyDestType(type: MappingDestType): boolean {
  return SQL_FAMILY_TYPES.includes(type);
}

/** The default schema a bare (unqualified) table name resolves into for `type` — "dbo" for SQL Server,
 *  "public" for PostgreSQL, "" for MySQL (no schema layer distinct from the database) and for any
 *  non-relational type (never actually schema-qualified; callers only invoke this for SQL-family types). */
export function defaultSchemaFor(type: MappingDestType): string {
  // Fabric Warehouse defaults to dbo exactly as SQL Server does — FabricDestinationSettings.WarehouseSchema
  // uses the same default, so a bare name qualified here and one the backend derives always agree.
  return type === 'sql' || type === 'fabricwarehouse' ? 'dbo' : type === 'postgres' ? 'public' : '';
}

/** Schema-qualifies a bare table name for `type`'s own default schema. Already-qualified names
 *  (containing a ".") are returned completely unchanged, regardless of `type` — never re-qualified,
 *  never double-qualified, since a "." means either a real, deliberately-typed schema.table (SQL
 *  Server/PostgreSQL) or, for MySQL/anything else, a name this function has no business rewriting. */
export function qualifyTableName(name: string, type: MappingDestType): string {
  if (name.includes('.')) return name;
  const schema = defaultSchemaFor(type);
  return schema ? `${schema}.${name}` : name;
}

/** Splits a (possibly bare) table name into its schema/table parts, defaulting the schema via
 *  defaultSchemaFor() when none is given — mirrors SqlDestinationSchemaService.SplitTableName exactly,
 *  so a locally-synthesized preview (e.g. right after "Create a new table…", before the real backend
 *  response overwrites it) always agrees with what the backend will actually store. */
export function splitTableName(name: string, type: MappingDestType): { schemaName: string; tableName: string } {
  const dot = name.indexOf('.');
  if (dot >= 0) return { schemaName: name.slice(0, dot), tableName: name.slice(dot + 1) };
  return { schemaName: defaultSchemaFor(type), tableName: name };
}

/**
 * Reconciles a resource → target-table map across a destination-type switch between two SQL-family types
 * (SQL Server ⇄ MySQL ⇄ PostgreSQL) — a no-op for any other pair, including into/out of Mongo/CSV/Blob/
 * FHIR-native types, which have their own unrelated target shape and are never touched here.
 *
 * For each resource whose CURRENT target still exactly equals what qualifyTableName would have produced
 * from that resource's own catalog entry under `previousType` — i.e. it's still the untouched auto-seeded
 * default, never renamed/retargeted/recreated by the user — re-derives it for `type` instead: a resource
 * nobody has touched since it was auto-seeded as "dbo.Appointment" under SQL Server correctly becomes
 * "public.Appointment" the moment the destination switches to PostgreSQL.
 *
 * Any OTHER target — manually typed, renamed, created via "Create a new table…", or picked via "Select
 * Existing" — never matches that exact shape, so it's left completely untouched. This is deliberately a
 * pure string-equality check, not a "looks auto-generated" heuristic: a user who happens to manually type
 * exactly the same string the auto-default would have produced is indistinguishable from — and behaves
 * identically to — an untouched default, which is the same trade-off the rest of this screen already
 * makes wherever it can't otherwise tell "guessed" from "confirmed" apart (see isPrimaryTargetValid).
 *
 * `catalogSqlTableByResource` supplies each resource's raw catalog entry (DEST_RESOURCE_DEFS/
 * genericResourceDef's `sqlTable`, hand-authored as "dbo.X") — a resource missing from it is left alone.
 */
export function reconcileTargetsForDestTypeSwitch(
  targets: Record<string, string>,
  catalogSqlTableByResource: Record<string, string>,
  previousType: MappingDestType,
  type: MappingDestType,
): Record<string, string> {
  if (previousType === type || !isSqlFamilyDestType(previousType) || !isSqlFamilyDestType(type)) {
    return targets;
  }
  const next = { ...targets };
  for (const [resource, current] of Object.entries(targets)) {
    const catalogSqlTable = catalogSqlTableByResource[resource];
    if (!current || !catalogSqlTable) continue;
    const bare = catalogSqlTable.replace(/^dbo\./i, '');
    if (current === qualifyTableName(bare, previousType)) {
      next[resource] = qualifyTableName(bare, type);
    }
  }
  return next;
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

/** Reserved @token JsonPath values a 'default' row's targetField can carry instead of a real "$…" source
 *  path — mirrors JsonMappingEngine.IsSystemToken/its "@default" literal-constant special case exactly, so
 *  a token picked here means the identical thing at pipeline-run time. '@default' means "always write the
 *  literal defaultValue text below"; the rest are pipeline/runtime values (JsonMappingEngine's
 *  systemValues) with no literal to type in. */
export type DefaultValueToken =
  | '@default' | '@now' | '@runId' | '@resourceType' | '@sourceResourceId' | '@newGuid'
  | '@mappingProfileName' | '@mappingProfileId' | '@destinationObject' | '@sourceConnectionId' | '@triggeredBy';

export interface DefaultValuePreset {
  token: DefaultValueToken;
  label: string;
  description: string;
  /** The MappingValueType this preset always produces — lets checkColumnTypeCompatibility validate a
   *  default column exactly like a real mapped one. */
  valueType: string;
  /** True hides this preset from the "Set default value" picker (see VISIBLE_DEFAULT_VALUE_PRESETS) while
   *  leaving it fully functional everywhere else — JsonMappingEngine, CreateMappingProfileRequestValidator,
   *  and any column a profile already has saved with it keep working and keep displaying correctly (see
   *  field-mapping-list.component.ts's defaultTokenLabel, which always looks up the FULL list). A temporary
   *  UI curation, not a feature removal — flip back to false to re-offer a preset. */
  hidden?: boolean;
}

export const DEFAULT_VALUE_PRESETS: readonly DefaultValuePreset[] = [
  { token: '@default', label: 'Literal value', description: 'Always write the exact text you type below.', valueType: 'String' },
  { token: '@now', label: 'Current date/time (UTC)', description: 'The moment this record is written by the pipeline.', valueType: 'DateTime' },
  { token: '@runId', label: 'Pipeline run ID', description: "This execution's own run id.", valueType: 'String' },
  { token: '@resourceType', label: 'FHIR resource type', description: 'The resource type being mapped (e.g. "Patient").', valueType: 'String', hidden: true },
  { token: '@sourceResourceId', label: 'Source resource ID', description: "The source FHIR resource's own id.", valueType: 'String', hidden: true },
  // A fresh value per RECORD, not per run (unlike every other token above) — see JsonMappingEngine/
  // ConfiguredPipelineService, which regenerate it inside the per-resource loop. A row fanned out into
  // multiple destination rows via RepeatParent shares one value across all of them (same limitation @now/
  // @runId already have), since the token resolves once per source resource, not per fanned-out row.
  { token: '@newGuid', label: 'New GUID', description: 'A fresh random GUID generated for each record — useful as a surrogate key when no natural id exists.', valueType: 'String' },
  { token: '@mappingProfileName', label: 'Mapping profile name', description: 'The name of the mapping profile that wrote this row.', valueType: 'String', hidden: true },
  { token: '@mappingProfileId', label: 'Mapping profile ID', description: 'The GUID of the mapping profile that wrote this row.', valueType: 'String', hidden: true },
  { token: '@destinationObject', label: 'Destination table/object', description: 'The table or file this profile writes to (e.g. "dbo.Patient").', valueType: 'String', hidden: true },
  { token: '@sourceConnectionId', label: 'Source connection ID', description: 'The GUID of the source connection this data came from.', valueType: 'String', hidden: true },
  // Only populated by the Configured Pipeline (ConfiguredPipelineService) — the Runtime Plane's transform
  // executor has no trigger identity to report, so this resolves to null there (like any absent token).
  // TriggeredBy is itself nullable at run time (some scheduled/system runs carry none) — see
  // CreateMappingProfileRequestValidator's NOT NULL check, which does NOT exempt this token the way it does
  // every other one above.
  { token: '@triggeredBy', label: 'Triggered by', description: 'Who or what started this run — a user, "scheduled", or "webhook". May be empty for some automated runs.', valueType: 'String', hidden: true },
];

/** What the "Set default value" picker actually offers — DEFAULT_VALUE_PRESETS minus anything currently
 *  `hidden`. See DefaultValuePreset.hidden's own doc comment for why this is a UI curation, not a removal. */
export const VISIBLE_DEFAULT_VALUE_PRESETS: readonly DefaultValuePreset[] =
  DEFAULT_VALUE_PRESETS.filter(p => !p.hidden);

export interface MappingRow {
  resource: string;
  /** Ordered; length 1 = simple map, length > 1 = join. Empty when mode is 'childJson' or 'default'. */
  sources: MappingSourceRef[];
  /** 'default' means this column always writes a fixed value (literal or pipeline/runtime @token,
   *  see defaultToken) instead of anything read from the source payload — mutually exclusive with a real
   *  source mapping by construction, since a row is wholesale replaced (never merged) whenever its column
   *  is remapped or re-defaulted (see FieldMappingCanvasComponent.replaceRow/submitDefaultValue). */
  mode: 'value' | 'childJson' | 'default';
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
  /** Only meaningful when mode === 'default' AND defaultToken === '@default' — the literal text written
   *  for every record. (Also round-trips a profile-authored fallback-when-missing default on a 'value' row,
   *  unrelated to this feature — see JsonMappingEngine's resolved.Count === 0 branch.) */
  defaultValue?: string | null;
  /** Only meaningful when mode === 'default' — which @token this column always resolves to. */
  defaultToken?: DefaultValueToken;
  /** Only meaningful when mode === 'default' — the MappingValueType this default column writes, since
   *  there's no source field to read one from (see effectiveMappingValueType). */
  defaultValueType?: string;
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
  /**
   * How this row's JSON value is materialized in the destination column — only meaningful when the row's
   * effective ValueType is 'Json' (see effectiveMappingValueType) AND the destination stores structure
   * natively, which today means MongoDB only. Undefined everywhere else, and undefined means 'string':
   * every mapping authored before this option existed keeps the single, escaped-text behaviour it had.
   * Round-trips through dest_mappings_v2 (the full MappingRow) and reaches the backend as the
   * "json=document" marker serializeRowsFlat appends to `format` (see MappingFieldFormat on the backend).
   */
  jsonWriteMode?: JsonColumnWriteMode;
}

/** See MappingRow.jsonWriteMode — mirrors the backend's JsonColumnWriteMode enum. */
export type JsonColumnWriteMode = 'string' | 'document';

/** The marker serializeRowsFlat appends to a field's `format` to select 'document' — read back by the
 *  backend's MappingFieldFormat.ReadJsonWriteMode, which treats its absence as 'string'. */
export const JSON_WRITE_MODE_DOCUMENT_MARKER = 'json=document';

/** True when `row` is one the MongoDB "store as JSON string / as a JSON document" choice applies to: its
 *  value reaches the destination as JSON text (a whole-node mapping, or any Json-typed source field), so
 *  there is something to either keep as text or expand into a real sub-document.
 *
 *  A row already carrying an explicit jsonWriteMode counts too, whatever its sources currently declare:
 *  only a Json-valued row can ever have been given one, and a row rebuilt from the Mapping JSON summary
 *  comes back with no source valueType at all (sourceRefFromPath doesn't round-trip it), so without this
 *  a restored 'value' row would drop the user's choice the next time it was saved. */
export function supportsJsonWriteMode(row: MappingRow): boolean {
  // A join's value is `string.Join(delimiter, pieces)` on the backend (JsonMappingEngine.ResolveJoinedFields)
  // — a delimited string by construction, never a JSON document, whatever its first source's own type says.
  // Checked before everything else so a single-source Json row that later gains a second source stops
  // offering (and stops sending) a choice that could no longer mean anything.
  if (row.sources.length > 1) return false;
  return effectiveMappingValueType(row) === 'Json' || row.jsonWriteMode !== undefined;
}

/** The JSON write mode a backend field's `format` marker bag selects — the client-side mirror of the
 *  backend's MappingFieldFormat.ReadJsonWriteMode, and deliberately the same substring test rather than an
 *  equality check: `format` carries a ';'-separated BAG of markers, so the real stored values include
 *  "json=document" alone AND compound forms like "wholeNodeAsJson;json=document" (see
 *  formatWithJsonWriteMode, which preserves whatever markers were already there, and
 *  MappingImportService.BuildJsonPathAndFormat, which stamps the column-mode marker). Anything without the
 *  marker — a null/blank format included — is 'string'.
 *
 *  Matches whole ';'-separated SEGMENTS, not a raw substring of the joined value, mirroring the backend's
 *  ReadJsonWriteMode and JsonMappingEngine.ParseDelimiter. A segment can carry arbitrary user text — a
 *  "joinedFields;delimiter=X" field takes everything after the first '=' as its delimiter — so a substring
 *  test would let a delimiter of "json=document" flip an unrelated column into document storage. */
export function jsonWriteModeFromFormat(format: string | null | undefined): JsonColumnWriteMode {
  return (format ?? '')
    .split(';')
    .some(segment => segment.trim().toLowerCase() === JSON_WRITE_MODE_DOCUMENT_MARKER)
    ? 'document'
    : 'string';
}

/** `row`'s effective JSON write mode, defaulting to today's behaviour ('string') for every row that has
 *  never made the choice. */
export function resolveJsonWriteMode(row: MappingRow): JsonColumnWriteMode {
  return row.jsonWriteMode === 'document' ? 'document' : 'string';
}

/** The `format` value carrying `mode`, preserving whatever other markers `format` already held (e.g. the
 *  "wholeNodeAsJson"/"aggregate=csv" column-mode markers the mapping-profiles import path stamps).
 *  Returns undefined when there is nothing to record — a 'string' row with no pre-existing format keeps
 *  sending no `format` at all, exactly as before this option existed. */
export function formatWithJsonWriteMode(
  format: string | null | undefined,
  mode: JsonColumnWriteMode,
): string | undefined {
  const base = (format ?? '')
    .split(';')
    .map(part => part.trim())
    .filter(part => part.length > 0 && !/^json=/i.test(part));

  if (mode === 'document') base.push(JSON_WRITE_MODE_DOCUMENT_MARKER);
  return base.length > 0 ? base.join(';') : undefined;
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
  arrayPolicy: string; approximated: boolean; isUpsertKey: boolean; isRequired?: boolean;
  defaultValue?: string | null;
  format?: string;
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

    // A column the destination declares NOT NULL has to be marked required, or the save gate rejects the
    // whole workflow ("'content' does not allow NULLs — mark this field as required or supply a default
    // value." from CreateMappingProfileRequestValidator) — and this wizard has no per-row "required"
    // toggle a user could satisfy it with by hand, so mapping anything onto such a column (e.g. the whole
    // payload as JSON into a HAPI-style `content text NOT NULL`) was previously unsaveable. Read off the
    // live schema exactly like isUpsertKey above, and OMITTED rather than sent as false when the column is
    // nullable or its schema isn't known here, so workflow-build-assembler's own
    // `row.isRequired ?? row === idRow` default still decides those (an explicit false would strip the id
    // row's required flag). IsRequired only ever adds a per-record mapping error message at run time
    // (JsonMappingEngine) — it never changes what gets written.
    const targetColumn = targetTable?.columns.find(c => c.name === row.targetName);
    const isRequired = row.isRequired ?? (targetColumn?.isNullable === false ? true : undefined);
    const isDefault = row.mode === 'default';
    // The MongoDB "store this JSON as a real sub-document, not as escaped text" choice rides the same
    // `format` marker channel the mapping-profiles import path already uses for column-mode markers, so it
    // needs no new field anywhere on the wire (see formatWithJsonWriteMode / the backend's
    // MappingFieldFormat). A row that never made the choice — i.e. every row saved before this existed, and
    // every row on a non-Mongo destination — produces undefined here and so sends no `format` at all,
    // leaving the request byte-for-byte what it was. Strictly additive for that reason: this is the only
    // condition under which serializeRowsFlat has ever emitted `format`.
    const format = supportsJsonWriteMode(row) && resolveJsonWriteMode(row) === 'document'
      ? formatWithJsonWriteMode(row.format, 'document')
      : undefined;
    return {
      resource: row.resource,
      // A default column has no real source field — the token itself (e.g. "@now") stands in as both the
      // human-readable field label and the wire jsonPath, exactly mirroring how a token round-trips through
      // JsonMappingEngine.IsSystemToken on the backend.
      field: isDefault ? (row.defaultToken ?? '@default') : (primary?.label ?? ''),
      path: isDefault ? (row.defaultToken ?? '@default') : (primary?.fhirPath ?? row.childNodeId ?? ''),
      target: targetTableName,
      column: row.targetName,
      jsonPath: isDefault ? (row.defaultToken ?? '@default') : primary?.jsonPath,
      // `format && 'Json'`: emitting the document marker and declaring anything but Json would be
      // self-contradictory, and the backend reads BOTH — MappedMongoDestinationWriter.ResolveDocumentJsonColumns
      // only honours the marker on a ValueType=Json field, and JsonMappingEngine.ConvertElement only hands the
      // value through as raw JSON text for that same type. This matters for a row rebuilt from the Mapping JSON
      // summary, which doesn't round-trip a source's valueType (sourceRefFromPath): such a row still carries the
      // user's 'document' choice but would otherwise fall back to the assembler's path-guessed 'String',
      // silently downgrading to escaped text while the UI kept showing "JSON document".
      valueType: format
        ? 'Json'
        : (isDefault ? row.defaultValueType : (arrayPolicy === 'StoreJson' ? 'Json' : primary?.valueType)),
      arrays: primary?.arrays,
      arrayPolicy,
      approximated,
      isUpsertKey,
      ...(format ? { format } : {}),
      ...(isRequired ? { isRequired: true } : {}),
      // Previously dropped here even though MappingRow already carried it (see its own doc comment) — every
      // caller downstream (workflow-build-assembler.service.ts's MappingFieldRequest.defaultValue,
      // JsonMappingEngine's literal-fallback and "@default" branches) has always expected this, so a
      // profile-authored fallback default on an ordinary 'value' row round-trips correctly too, not just a
      // 'default'-mode row's own literal.
      ...(row.defaultValue ? { defaultValue: row.defaultValue } : {}),
      ...(genuineRelation ? {
        parentTable: genuineRelation.parentTable,
        parentKeyColumn: genuineRelation.parentColumn,
        foreignKeyColumn: genuineRelation.foreignKeyColumnName,
      } : {}),
      // Round-tripped through to workflow-build-assembler.service.ts, which resolves it into the referenced
      // resource's own table/id column (MappingFieldRequest.referenceLookupTable/referenceLookupKeyColumn) —
      // without this, the field-mapping-list "which resource does this reference?" picker has no effect at
      // all on what actually gets saved, and a FHIR reference column keeps writing raw "Patient/xyz" strings
      // (or, worse, NULL into a NOT NULL FK column) forever, no matter what the user picked in that dropdown.
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
  if (row.mode === 'default') {
    return { arrayPolicy: 'Scalar', approximated: false };
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

/** The effective, backend-facing ValueType for a mapping row — the same one serializeRowsFlat() actually
 *  sends to the API (see its own `valueType: arrayPolicy === 'StoreJson' ? 'Json' : primary?.valueType`):
 *  'Json' for a childJson row (StoreJson comes exclusively from mode === 'childJson' — see
 *  resolveArrayPolicy), the row's own declared defaultValueType for a 'default' row (no source field to
 *  read one from), the primary source field's own valueType for a 'value' row. */
export function effectiveMappingValueType(row: MappingRow): string | undefined {
  if (row.mode === 'childJson') return 'Json';
  if (row.mode === 'default') return row.defaultValueType;
  return row.sources[0]?.valueType;
}

/**
 * True when a Json-shaped value (a childJson row, or a transformation rule that declares a Json output)
 * is safe to write into `column` even though its own declared mappingValueType is the generic "String"
 * every character-string SQL type collapses to — no destination type/schema probe in this codebase has
 * ever produced "Json" for any real column (see SqlDestinationSchemaService.MapSqlServerType/
 * MapPostgresType/MapMySqlType and this file's own field-mapping-canvas.component.ts mapSqlServerType
 * preview copy: every one of them falls through to "String" for every character type, including a native
 * `jsonb`), so an exact-match "Json === column's declared type" comparison would reject EVERY Json
 * mapping onto EVERY destination, including one deliberately sized to hold it (e.g. SQL Server
 * nvarchar(max)).
 *
 * Scoped narrowly to an UNBOUNDED string column — `maxLength` EXPLICITLY `null` (never merely absent/
 * `undefined` — see below), the same signal the backend's own CheckStructuredOutputFitsColumn
 * (CreateMappingProfileRequestValidator.cs) already uses to mean "no truncation risk here". A
 * length-BOUNDED string column (nvarchar(50), varchar(200), …) keeps failing exactly as before: genuine
 * truncation risk there, not a false positive. See this file's own regression tests
 * (field-mapping-model.spec.ts) for both the still-rejected bounded case and this newly-allowed
 * unbounded one.
 *
 * `undefined` is deliberately NOT treated the same as `null` here, even though `column.maxLength` is
 * optional on this function's own parameter type: a REAL DestinationColumn (destination-schema.service.ts)
 * always carries an explicit `number | null` for this field — `undefined` only ever means a caller
 * (a test fixture, or any other narrower object literal) simply didn't populate it, which must keep
 * behaving exactly as it did before this exception existed, not silently start being treated as
 * "definitely unbounded, therefore Json-safe".
 *
 * `maxLength === null` alone is NOT a reliable "unbounded" signal across every engine — MySQL's
 * TEXT/MEDIUMTEXT/LONGTEXT never report a null max length at all (unlike SQL Server nvarchar(max)'s -1,
 * or PostgreSQL's genuinely NULL character_maximum_length for `text`); MySQL always reports a real, if
 * enormous, number (65,535 / 16,777,215 / 4,294,967,296) for these, because their capacity is fixed by the
 * type keyword itself, not an independently configurable length the way varchar(n) is. So those three (plus
 * SQL Server's own legacy `ntext`, whose CHARACTER_MAXIMUM_LENGTH is likewise a real, non-null number) are
 * additionally recognized by DATA TYPE NAME, regardless of whatever maxLength happens to be reported —
 * exported so every mapping-save surface (this file's own checkColumnTypeCompatibility, and the
 * mapping-profiles dialogs that don't share its MappingRow shape) applies the identical rule.
 * Deliberately excludes MySQL's much smaller TINYTEXT (255 chars) — genuinely too small for arbitrary
 * JSON, so that one correctly keeps failing, same as any other length-bounded column.
 */
export function isJsonSafeForColumn(
  effectiveType: string,
  column: { dataType?: string; mappingValueType?: string; maxLength?: number | null },
): boolean {
  if (effectiveType.toLowerCase() !== 'json') return false;
  if ((column.mappingValueType ?? '').toLowerCase() !== 'string') return false;
  if (column.maxLength === null) return true;

  const family = (column.dataType ?? '').trim().toLowerCase().split('(')[0];
  return family === 'text' || family === 'mediumtext' || family === 'longtext' || family === 'ntext';
}

/**
 * Mirrors CreateMappingProfileRequestValidator.ValidateAgainstDestinationSchemaAsync's own strict
 * ValueType check (the backend's /workflows/build validator) — same exact-match rule (no implicit
 * widening: Integer→Decimal is rejected exactly like String→Integer is), with the one deliberate
 * exception in isJsonSafeForColumn above. Extracted out of DestinationWizardComponent.validateMappingForSave
 * so the exact rule it enforces is independently testable without standing up that component's full DI graph.
 *
 * Compares row's EFFECTIVE ValueType (effectiveMappingValueType — 'Json' for childJson, not just a
 * 'value' row's own source type) against the column's mappingValueType, so a childJson mapping onto a
 * column that doesn't accept Json (e.g. a plain, length-bounded varchar) is caught here too, instead of
 * only surfacing once the whole workflow is saved.
 *
 * Returns the exact user-facing error text on a mismatch, or null when compatible / when there's nothing
 * meaningful to compare yet (no column type metadata, or no known effective type — e.g. an empty row).
 */
/**
 * @param ruleExpectedType The `expectedValueType` of a Field-scope transformation rule already resolved
 *   for this exact connector (destination-wizard.component.ts's resolveRuleExpectedTypesForSave), if
 *   any: `undefined` — no rule on this field, fall back to comparing the raw source type (original
 *   behavior); `null` — a rule exists but declares no output type, so there's nothing to validate against
 *   and the raw-type check is skipped entirely (trust the rule); a string — the rule's declared output
 *   type, compared against the column instead of the raw source type, since that's what actually reaches
 *   the column at runtime. Without this, a rule that legitimately changes the value's shape (Date ->
 *   Integer for an age calculation, String -> Json for a parsed name, etc.) always failed this check,
 *   even though the transform makes the raw-type mismatch irrelevant.
 */
export function checkColumnTypeCompatibility(
  row: MappingRow,
  column: { dataType: string; mappingValueType?: string; maxLength?: number | null } | undefined,
  ruleExpectedType?: string | null,
): string | null {
  if (!column?.mappingValueType) return null;

  if (ruleExpectedType !== undefined) {
    if (
      ruleExpectedType === null ||
      ruleExpectedType.toLowerCase() === column.mappingValueType.toLowerCase() ||
      isJsonSafeForColumn(ruleExpectedType, column)
    ) {
      return null;
    }
    return (
      `"${row.targetName}" on ${row.tableName} is a ${column.dataType} column (expects ${column.mappingValueType}), ` +
      `but its transformation rule declares an output type of ${ruleExpectedType} — fix the rule's Expected ` +
      `output type, or retarget to a ${ruleExpectedType}-compatible column.`
    );
  }

  const sourceValueType = effectiveMappingValueType(row);
  if (
    !sourceValueType ||
    sourceValueType.toLowerCase() === column.mappingValueType.toLowerCase() ||
    isJsonSafeForColumn(sourceValueType, column)
  ) {
    return null;
  }
  return (
    `"${row.targetName}" on ${row.tableName} is a ${column.dataType} column (expects ${column.mappingValueType}), ` +
    `but "${row.sources[0]?.label ?? row.targetName}" is mapped as ${sourceValueType} — pick a compatible ` +
    `source field or retarget to a ${sourceValueType}-compatible column.`
  );
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
