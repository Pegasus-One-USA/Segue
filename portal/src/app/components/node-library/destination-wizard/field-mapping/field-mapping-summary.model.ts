// ── Mapping summary (the canonical "Mapping JSON") ──────────────────────────────
// The one save/load contract for the Map Fields screen: a document aggregating every mapped resource's
// destination tables (new-vs-existing, parent/child relation), the schema DDL implied by what's new
// (tablesToCreate/columnsToAdd), a dependency-ordered processing order (parents before children), and
// every mapped column (direct/joined/whole-node-as-JSON, with array/instance handling). This replaces
// the old per-resource, preview-only MappingExport — this is aggregated across every resource with at
// least one mapping, under one {source, destination, mappings[]} envelope, and is the one shape both
// "Save mapping" and "Load mapping" (Edit) round-trip through. Independent of dest_mappings/
// dest_mappings_v2 (still feed workflow-build-assembler.service.ts unmodified) and of MappingSnapshot
// (whole-screen UI-state round-trip, kept as its own separate feature).

import { DestinationTable, DestinationColumn } from '../../../../services/destination-schema.service';
import type { ResourceFieldDef } from '../destination-wizard.component';
import { MappingRow, MappingInstanceSelection, MappingSourceRef, MappingDestType, qualifyTableName } from './field-mapping-model';
import { FmTreeNode, buildForest, findNode } from './field-mapping-tree.util';
import { dependencyRankFor } from '../resource-dependency.config';

/** SQL Server/MySQL/PostgreSQL all use the relational (sqlTables/columns, dbo-qualified) path here — only
 *  CSV, Mongo, Medplum, and FHIR Repository (columnless/schemaless FHIR) fall back to the flat/generic defaults. */
function isRelational(destType: MappingDestType): boolean {
  return destType !== 'csv' && destType !== 'mongo' && destType !== 'medplum' && destType !== 'fhir';
}

/** A table created as a child of another table — the only place this relationship is known client-side
 *  (the backend's CreateTableAsync response never echoes it back). Keyed by table full name wherever
 *  it's tracked as state. */
export interface ChildTableRelation {
  parentTable: string;
  parentColumn: string;
  foreignKeyColumnName: string;
}

export interface MappingSchemaColumnDef {
  name: string;
  dataType: string;
  isPrimaryKey: boolean;
  isForeignKey: boolean;
  references: string | null;
}

export interface MappingSchemaTableToCreate {
  name: string;
  relation: { childColumn: string; parentTable: string; parentColumn: string } | null;
  columns: MappingSchemaColumnDef[];
}

export interface MappingSchemaColumnToAdd {
  table: string;
  name: string;
  dataType: string;
}

export interface MappingSchemaChanges {
  tablesToCreate: MappingSchemaTableToCreate[];
  columnsToAdd: MappingSchemaColumnToAdd[];
  summary: string;
}

export interface MappingProcessingStep {
  step: number;
  table: string;
  level: number;
  dependsOn: string | null;
  note: string | null;
}

export interface MappingSummaryInstance {
  arrayContext: string;
  type: MappingInstanceSelection['type'];
  n?: number;
  field?: string;
  op?: MappingInstanceSelection['op'];
  value?: string;
  aggregate?: 'rows' | 'csv';
}

export interface MappingSummaryColumn {
  column: string;
  mode: 'directField' | 'joinedFields' | 'wholeNodeAsJson';
  /** directField / joinedFields only. */
  sources?: string[];
  /** wholeNodeAsJson only. */
  sourceNode?: string;
  /** joinedFields only. */
  delimiter?: string;
  instance: MappingSummaryInstance | null;
  /** Set when this column is a FHIR reference that must be resolved against another mapped resource's
   *  own table + id column at write time — see MappingRow.referencesResource for how this is derived. */
  referenceLookup?: { table: string; keyColumn: string };
  /** Mirrors MappingRow.isUpsertKey — present (true) only on the one column the user explicitly
   *  designated as the resource's upsert key via the target card's toggle. Omitted (not false) on every
   *  other column, matching referenceLookup's spread convention above. */
  isUpsertKey?: boolean;
}

export interface MappingSummaryTable {
  name: string;
  isNew: boolean;
  /** Explicit "this is the resource's primary table/collection" flag, set from the wizard's own
   *  authoritative targetByResource signal at save time — added because relation-based inference
   *  (previously the only signal: "primary = whichever table has no genuine child relation") broke once a
   *  Mongo destination could have a genuinely independent extra collection with no relation at all, making
   *  both it and the real primary look identical on reload. Optional because an already-saved document from
   *  before this field existed won't have it — applyMappingSummaryDocument falls back to the old
   *  relation-based inference when no table in the entry carries it. */
  isPrimary?: boolean;
  relation: { childColumn: string; parentTable: string; parentColumn: string } | null;
  columns: MappingSummaryColumn[];
}

export interface MappingResourceEntry {
  resourceType: string;
  /** This resource's position in dependency order (see dependencyRankFor) — 0 for anything with no
   *  required dependency (Patient, Practitioner, …), otherwise 1 + the highest rank among its direct
   *  required dependencies. Mirrors processingOrder's own table-level ordering, one level up. */
  rank: number;
  generatedAt: string;
  schemaChanges: MappingSchemaChanges;
  processingOrder: MappingProcessingStep[];
  destination: { type: MappingDestType; label: string };
  tables: MappingSummaryTable[];
  /** The MappingProfile this resource's own Field Mapping node last saved (round-tripped from the node's
   *  own mappingProfileIds), when known — null on a genuinely first-ever save. The backend always updates
   *  THIS exact profile rather than searching for "the" profile matching (resourceType, sourceConnectionId,
   *  destinationId): that triple can be shared by more than one workflow, so a search-based match would
   *  silently attach to — and overwrite — a different workflow's profile. See MappingImportService. */
  existingMappingProfileId?: string | null;
}

export interface MappingSummaryDocument {
  source: string;
  destination: string;
  /** The upstream source connection this whole document was built against — the pipeline's launch
   *  source node's saved connection id, or null if none is wired up yet. */
  sourceConnectionId: string | null;
  /** The destination configuration this document targets, when attaching to an already-saved
   *  destination rather than a brand-new one being created in the same step — null otherwise. */
  destinationId: string | null;
  mappings: MappingResourceEntry[];
}

const DEFAULT_SQL_TYPE = 'nvarchar(max)';
const DEFAULT_CSV_TYPE = 'string';
const DEFAULT_ID_TYPE = 'bigint';

// ── name helpers — the summary document always uses bare table names (no "dbo." schema prefix),
// matching the target JSON's convention; the app's own state keeps schema-qualified full names. ──

function bareName(fullName: string): string {
  const i = fullName.lastIndexOf('.');
  return i === -1 ? fullName : fullName.slice(i + 1);
}

// Re-qualification on load goes through the shared qualifyTableName (field-mapping-model.ts) — the same
// function _qualifyDefaultTable/submitCreateTable use to qualify on creation, so a mapping saved (bare)
// under one destType and reopened under the same destType always comes back exactly as it went out. This
// used to be a private, SQL-Server-only qualify() here that never added "public." for PostgreSQL, so
// reopening a PostgreSQL mapping restored "Appointment" instead of "public.Appointment".

// ── array-context resolution — walks a node's id up through its ancestors (a leaf's own isArray is
// always false, so starting at a leaf and starting at a group both fall through to the same walk) to
// find the nearest enclosing repeating group, exactly like the reference HTML's nearestArrayNode(). ──

export function nearestArrayGroupId(forest: FmTreeNode[], nodeId: string): string | null {
  let id: string | null = nodeId;
  while (id) {
    const node = findNode(forest, id);
    if (node?.kind === 'group' && node.isArray) return node.id;
    const dot = id.lastIndexOf('.');
    id = dot === -1 ? null : id.slice(0, dot);
  }
  return null;
}

function arrayContextFor(row: MappingRow, forest: FmTreeNode[]): string | null {
  const startId = row.mode === 'childJson' ? row.childNodeId : row.sources[0]?.fhirPath;
  return startId ? nearestArrayGroupId(forest, startId) : null;
}

function toSummaryInstance(row: MappingRow, arrayContext: string | null): MappingSummaryInstance | null {
  if (!arrayContext) return null;
  const instance = row.instance ?? { type: 'first' };
  const out: MappingSummaryInstance = { arrayContext, type: instance.type };
  if (instance.n !== undefined) out.n = instance.n;
  if (instance.field !== undefined) out.field = instance.field;
  if (instance.op !== undefined) out.op = instance.op;
  if (instance.value !== undefined) out.value = instance.value;
  if (instance.aggregate !== undefined) out.aggregate = instance.aggregate;
  return out;
}

/** A resource's own root table + the column its own "$.id" field targets — what a REFERENCE elsewhere
 *  resolves against. Computed once across every mapped resource (not per-resource) since a reference on
 *  one resource (e.g. Observation.subject.reference) points at ANOTHER resource (Patient) that may be
 *  processed before or after it in the resources loop. */
interface ResourceKeyInfo {
  table: string;
  keyColumn: string;
}

function computeResourceKeyInfo(mappingRows: MappingRow[]): Record<string, ResourceKeyInfo> {
  const result: Record<string, ResourceKeyInfo> = {};
  for (const resource of new Set(mappingRows.map(r => r.resource))) {
    const idRow = mappingRows.find(r =>
      r.resource === resource && r.mode === 'value' && r.sources.length === 1
      && r.sources[0].fhirPath === `${resource}.id`);
    if (idRow) {
      result[resource] = { table: bareName(idRow.tableName), keyColumn: idRow.targetName };
    }
  }
  return result;
}

function toSummaryColumn(
  row: MappingRow, forest: FmTreeNode[], resourceKeyInfo: Record<string, ResourceKeyInfo>,
): MappingSummaryColumn {
  const instance = toSummaryInstance(row, arrayContextFor(row, forest));
  const keyInfo = row.referencesResource ? resourceKeyInfo[row.referencesResource] : undefined;
  // Spread so a non-reference column's output has no "referenceLookup" key at all (not merely one set to
  // undefined) — keeps the wire shape byte-for-byte identical to before this feature for every existing
  // mapping, and sidesteps any ambiguity in how a test's equality check treats an undefined-valued key.
  const referenceLookup = keyInfo ? { referenceLookup: { table: keyInfo.table, keyColumn: keyInfo.keyColumn } } : {};
  const upsertKey = row.isUpsertKey === true ? { isUpsertKey: true } : {};

  if (row.mode === 'childJson') {
    return { column: row.targetName, mode: 'wholeNodeAsJson', sourceNode: row.childNodeId ?? '', instance, ...referenceLookup, ...upsertKey };
  }
  const sources = row.sources.map(s => s.fhirPath);
  if (sources.length > 1) {
    return { column: row.targetName, mode: 'joinedFields', sources, delimiter: row.delimiter ?? ', ', instance, ...referenceLookup, ...upsertKey };
  }
  return { column: row.targetName, mode: 'directField', sources, instance, ...referenceLookup, ...upsertKey };
}

// ── per-resource table set + schema-change/processing-order derivation ──────────────────────────────

interface ResolvedTable {
  fullName: string;
  bare: string;
  isNew: boolean;
  relation: ChildTableRelation | undefined;
  columns: DestinationColumn[];
}

/** Reads a table's parent/FK relation straight off its own real column metadata — the same live-schema
 *  signal FieldMappingTargetCardComponent.detectedRelation renders as the canvas's "child of X via Y → Z"
 *  banner (shared by both so the two can never disagree on what "this table's parent" means). Whichever
 *  column actually has isForeignKey wins; a table normally has at most one FK back to its logical parent. */
export function detectRelationFromColumns(
  columns: readonly { name: string; isForeignKey?: boolean; references?: string | null }[],
): ChildTableRelation | undefined {
  for (const column of columns) {
    if (column.isForeignKey && column.references) {
      const lastDot = column.references.lastIndexOf('.');
      return {
        parentTable: column.references.slice(0, lastDot),
        parentColumn: column.references.slice(lastDot + 1),
        foreignKeyColumnName: column.name,
      };
    }
  }
  return undefined;
}

function resolveTable(
  fullName: string,
  sqlTables: DestinationTable[],
  childTableRelationsByTable: Record<string, ChildTableRelation>,
  destType: MappingDestType,
  resource: string,
  rootTableToResource: Record<string, string>,
): ResolvedTable {
  const known = sqlTables.find(t => t.fullName === fullName);
  // The live-probed FK metadata (isForeignKey/references, already on every real column) wins over
  // childTableRelationsByTable when both exist — it's read straight from the database, so it also
  // covers an already-existing table just picked from the dropdown, not only one created THIS session
  // via "Create a new table…" (childTableRelationsByTable's only source). Without this fallback, an
  // existing child table with no manually-declared relation looked exactly like a root table to
  // computeProcessingOrder's levelFor — tying it with the real root at level 1 and leaving the two
  // ordered alphabetically, silently picking the wrong one as this resource's DestinationObject whenever
  // the child table's name happened to sort first (e.g. "ObservationCategories" before "Observations").
  // Mirrors FieldMappingTargetCardComponent.relationToShow's identical detectedRelation() ?? relation()
  // priority, so the canvas's own "child of X via Y → Z" banner and what gets saved never disagree.
  const declaredRelation = (known && detectRelationFromColumns(known.columns)) ?? childTableRelationsByTable[fullName];
  // A genuine child-table relation only makes sense when its declared parent belongs to THIS SAME resource
  // (e.g. PatientAddress's parent "Patient", alongside Patient's own root mapping) — that's what actually
  // proves it's an intra-resource array fan-out, not a real SQL foreign key picked up from live-schema
  // probing that happens to point at a completely different resource's own root table (e.g.
  // Encounter.PatientId -> Patient.Id: Encounter is its own independent, top-level resource, not a child
  // sub-table of Patient's mapping). A cross-resource reference like that belongs on the REFERENCING FIELD
  // itself (see referencesResource/referenceLookup), never as a table-level relation — treating it as one
  // here would make this table's OWN root/primary mapping unrecoverable (every root-table lookup in this
  // file is literally "the one table with no relation"). Checked against rootTableToResource (derived from
  // the caller's own targetByResource, the independently-maintained "which table is each resource's
  // designated root" signal) rather than "does some row happen to target the parent table" — a resource
  // can legitimately have its root table declared with zero directly-mapped fields on it.
  const parentOwner = declaredRelation ? rootTableToResource[declaredRelation.parentTable] : undefined;
  const relation = declaredRelation && (parentOwner === undefined || parentOwner === resource)
    ? declaredRelation
    : undefined;
  return {
    fullName,
    bare: bareName(fullName),
    isNew: known ? known.origin === 'userCreated' : true,
    relation,
    columns: known?.columns ?? [{
      name: 'Id',
      dataType: isRelational(destType) ? DEFAULT_ID_TYPE : DEFAULT_CSV_TYPE,
      mappingValueType: 'string', isNullable: false, maxLength: null,
    }],
  };
}

function buildSchemaChanges(tables: ResolvedTable[], destType: MappingDestType): MappingSchemaChanges {
  const fallbackType = isRelational(destType) ? DEFAULT_SQL_TYPE : DEFAULT_CSV_TYPE;

  const tablesToCreate: MappingSchemaTableToCreate[] = tables.filter(t => t.isNew).map(t => ({
    name: t.bare,
    relation: t.relation
      ? { childColumn: t.relation.foreignKeyColumnName, parentTable: bareName(t.relation.parentTable), parentColumn: t.relation.parentColumn }
      : null,
    columns: t.columns.map(c => ({
      name: c.name,
      dataType: c.dataType || fallbackType,
      isPrimaryKey: c.name === 'Id',
      isForeignKey: !!t.relation && c.name === t.relation.foreignKeyColumnName,
      references: (t.relation && c.name === t.relation.foreignKeyColumnName)
        ? `${bareName(t.relation.parentTable)}.${t.relation.parentColumn}` : null,
    })),
  }));

  const columnsToAdd: MappingSchemaColumnToAdd[] = tables.filter(t => !t.isNew).flatMap(t =>
    t.columns.filter(c => c.origin === 'userCreated').map(c => ({ table: t.bare, name: c.name, dataType: c.dataType || fallbackType })),
  );

  const tableWord = (n: number) => n === 1 ? 'table' : 'tables';
  const columnWord = (n: number) => n === 1 ? 'column' : 'columns';
  return {
    tablesToCreate,
    columnsToAdd,
    summary: `${tablesToCreate.length} new ${tableWord(tablesToCreate.length)} to create, ${columnsToAdd.length} new ${columnWord(columnsToAdd.length)} on existing tables`,
  };
}

function computeProcessingOrder(tables: ResolvedTable[]): MappingProcessingStep[] {
  const byBare = new Map(tables.map(t => [t.bare, t]));
  const levelOf = new Map<string, number>();

  function levelFor(bare: string, seen: Set<string>): number {
    const cached = levelOf.get(bare);
    if (cached !== undefined) return cached;
    const table = byBare.get(bare);
    if (!table?.relation || seen.has(bare)) { levelOf.set(bare, 1); return 1; }
    const parentBare = bareName(table.relation.parentTable);
    const parentOnEntry = byBare.has(parentBare);
    const level = parentOnEntry ? 1 + levelFor(parentBare, new Set(seen).add(bare)) : 1;
    levelOf.set(bare, level);
    return level;
  }

  return tables
    .map(t => ({
      table: t.bare,
      level: levelFor(t.bare, new Set()),
      dependsOn: t.relation ? bareName(t.relation.parentTable) : null,
      note: (t.relation && !byBare.has(bareName(t.relation.parentTable)))
        ? `parent "${bareName(t.relation.parentTable)}" has no mappings for this resource — add it or this can't be populated`
        : null,
    }))
    .sort((a, b) => a.level - b.level || a.table.localeCompare(b.table))
    .map((o, i) => ({ step: i + 1, ...o }));
}

/**
 * Drops any mapping row whose target column doesn't correspond to a real (or intentionally new-but-not-yet-
 * created) column on its target table — e.g. a column renamed/dropped directly in the database after the
 * row was created, left behind as a ghost mapping in the canvas. Without this, a save can silently persist
 * (and a workflow run can fail on) a column that no longer exists anywhere. A table this session hasn't
 * probed/created at all yet (no entry in sqlTables) is treated as fully new — every one of its rows is kept,
 * since there's no existing schema yet to validate against. Deliberately NOT scoped to any one resource —
 * called once, on every save, across every mapped resource.
 *
 * Only a table whose columns came from an actual LIVE schema probe (origin: 'probed' — set solely by
 * DestinationWizardComponent._refreshSqlTablesFromLiveSchema, which only ever runs for SQL-family
 * destinations) is treated as authoritative enough to say a column is genuinely gone. A table known only
 * from a restored Mapping JSON (origin: 'userCreated' or undefined — see applyMappingSummaryDocument) is
 * just a snapshot of whatever ONE save happened to know about, not a live view of the real schema — for a
 * destination table more than one resource shares (a single CSV output table is the common case: it has
 * no live schema to probe at all, so its columns are ALWAYS restored-only, never 'probed'), that snapshot
 * only ever reflects whichever resource(s) were mapped as of that earlier save. Pruning against it would
 * remove a perfectly valid row the moment a second resource maps a new column onto the same shared table,
 * before the next full reopen ever gets a chance to fold that column into a fresh snapshot. Not scoped by
 * destination type — a SQL/MySQL table this session hasn't (re)probed yet gets exactly the same "insufficient
 * evidence, keep it" treatment as a brand-new table does, per the table-not-found branch above.
 */
export function pruneOrphanedMappingRows(mappingRows: MappingRow[], sqlTables: DestinationTable[]): MappingRow[] {
  const tablesByName = new Map(sqlTables.map(t => [t.fullName, t]));
  return mappingRows.filter(row => {
    const table = tablesByName.get(row.tableName);
    return !table || table.origin !== 'probed' || table.columns.some(c => c.name === row.targetName);
  });
}

// ── build ─────────────────────────────────────────────────────────────────────────────────────────

export interface BuildMappingSummaryParams {
  sourceVendor: string;
  destType: MappingDestType;
  destLabel: string;
  mappingRows: MappingRow[];
  sqlTables: DestinationTable[];
  childTableRelationsByTable: Record<string, ChildTableRelation>;
  availableFields: (resource: string) => ResourceFieldDef[];
  /** The pipeline's launch source node's saved connection id — null if no source is wired up yet. */
  sourceConnectionId: string | null;
  /** The already-saved destination configuration this mapping targets — null when building a brand-new
   *  destination in the same step rather than attaching to an existing one. */
  destinationId: string | null;
  /** Which table each resource has designated as its own root/primary target — the ground truth resolveTable
   *  uses to tell a genuine same-resource child-table relation (e.g. PatientName's parent "Patient") apart
   *  from a live-schema FK that happens to point at a DIFFERENT resource's own root table (e.g. Encounter's
   *  FK to Patient — a cross-resource reference, not a table-level relation). Optional and empty by default
   *  so existing callers/tests that never dealt with cross-resource FKs are unaffected. */
  targetByResource?: Record<string, string>;
  /** This Field Mapping node's own previously-saved profile id per resource type (from the node's
   *  mappingProfileIds), so each resource entry can tell the backend exactly which profile to update
   *  instead of letting it search by (resourceType, sourceConnectionId, destinationId) — a triple that can
   *  be shared by more than one workflow. Optional and empty by default for callers with nothing saved yet. */
  existingMappingProfileIdByResource?: Record<string, string>;
}

export function buildMappingSummaryDocument(params: BuildMappingSummaryParams): MappingSummaryDocument {
  const {
    sourceVendor, destType, destLabel, mappingRows, sqlTables, childTableRelationsByTable, availableFields,
    sourceConnectionId, destinationId, targetByResource = {}, existingMappingProfileIdByResource = {},
  } = params;
  const generatedAt = new Date().toISOString();

  // Rank order, not first-appearance order — a prerequisite resource (Patient) always sorts before
  // whatever requires it, matching processingOrder's own table-level ordering one level up.
  const resources = Array.from(new Set(mappingRows.map(r => r.resource)))
    .sort((a, b) => dependencyRankFor(a) - dependencyRankFor(b));

  // Computed once, across every mapped resource — a reference on one resource can point at another that's
  // processed earlier OR later in the loop below, so this can't be derived per-resource.
  const resourceKeyInfo = computeResourceKeyInfo(mappingRows);

  // Inverted once: table fullName -> whichever resource has designated it as their own root. Powers
  // resolveTable's cross-resource-relation guard below.
  const rootTableToResource: Record<string, string> = {};
  for (const [res, table] of Object.entries(targetByResource)) {
    rootTableToResource[table] = res;
  }

  const mappings: MappingResourceEntry[] = resources.map(resource => {
    const resourceRows = mappingRows.filter(r => r.resource === resource);
    const tableNames = Array.from(new Set(resourceRows.map(r => r.tableName)));
    const resolved = tableNames.map(name => resolveTable(name, sqlTables, childTableRelationsByTable, destType, resource, rootTableToResource));
    const forest = buildForest([resource], availableFields);

    const primaryTarget = targetByResource[resource];
    const tables: MappingSummaryTable[] = resolved.map(t => ({
      name: t.bare,
      isNew: t.isNew,
      isPrimary: t.fullName === primaryTarget,
      relation: t.relation
        ? { childColumn: t.relation.foreignKeyColumnName, parentTable: bareName(t.relation.parentTable), parentColumn: t.relation.parentColumn }
        : null,
      columns: resourceRows.filter(r => r.tableName === t.fullName).map(r => toSummaryColumn(r, forest, resourceKeyInfo)),
    }));
    // targetByResource[resource] not matching any of this resource's own tables (a caller with nothing set
    // yet, or a stale value) must never leave every table looking like an extra on reload — fall back to
    // the first one, same fallback applyMappingSummaryDocument itself uses.
    if (tables.length > 0 && !tables.some(t => t.isPrimary)) {
      tables[0].isPrimary = true;
    }

    return {
      resourceType: resource,
      rank: dependencyRankFor(resource),
      generatedAt,
      schemaChanges: buildSchemaChanges(resolved, destType),
      processingOrder: computeProcessingOrder(resolved),
      destination: { type: destType, label: destLabel },
      tables,
      existingMappingProfileId: existingMappingProfileIdByResource[resource] ?? null,
    };
  });

  return {
    source: sourceVendor,
    destination: destLabel,
    sourceConnectionId,
    destinationId,
    mappings,
  };
}

// ── apply (reverse) — reconstructs wizard state from a previously saved document. jsonPath/valueType
// and the full array-ancestor chain aren't part of this schema, so a reloaded-only row only gets its
// immediate arrayContext back, not the richer catalog metadata a live-mapped row would have — accepted,
// since that data has no place in this contract. ──

/**
 * A saved document's table.relation should only be trusted as a genuine child-table relation if its
 * declared parent is ALSO one of the SAME resource entry's own mapped tables — the load-time counterpart
 * to resolveTable's identical build-time guard. Without this, loading a document saved before that guard
 * existed (or saved by anything else that mis-derived a relation from a live-schema FK pointing at a
 * completely different resource's own table) would transiently restore the bad relation for one more
 * round-trip before the next save could clean it up.
 */
function isGenuineChildRelation(
  relation: { parentTable: string } | null,
  siblingTableNames: readonly string[],
): relation is { parentTable: string; parentColumn: string; childColumn: string } {
  return !!relation && siblingTableNames.includes(relation.parentTable);
}

/**
 * Finds this resource entry's primary table on reload. Prefers the explicit `isPrimary` flag
 * (buildMappingSummaryDocument's own authoritative targetByResource, round-tripped) when ANY table in the
 * entry carries it; falls back to the older "primary = whichever table has no genuine child relation"
 * inference for a document saved before that flag existed. The old inference alone degenerates once a
 * destination (Mongo) can have a genuinely independent extra table with no relation either — both it and
 * the real primary would look identical, and this would silently pick whichever happens to come first in
 * the array (see the ChildTableRelation-removal investigation this fixed).
 */
function resolvePrimaryTable(tables: readonly MappingSummaryTable[]): MappingSummaryTable | undefined {
  if (tables.some(t => t.isPrimary === true)) {
    return tables.find(t => t.isPrimary === true);
  }
  const siblingNames = tables.map(t => t.name);
  return tables.find(t => !isGenuineChildRelation(t.relation, siblingNames));
}

export interface AppliedMappingSummary {
  mappingRows: MappingRow[];
  targetByResource: Record<string, string>;
  extraTablesByGroup: Record<string, string[]>;
  sqlTables: DestinationTable[];
  childTableRelationsByTable: Record<string, ChildTableRelation>;
  selectedResources: string[];
}

function sourceRefFromPath(path: string, arrayContext: string | undefined, resource: string): MappingSourceRef {
  const label = path.slice(path.lastIndexOf('.') + 1);
  const relativeArray = arrayContext?.startsWith(`${resource}.`) ? arrayContext.slice(resource.length + 1) : arrayContext;
  return { fhirPath: path, label, arrays: relativeArray ? [relativeArray] : undefined };
}

function instanceFromSummary(instance: MappingSummaryInstance | null | undefined): MappingInstanceSelection | undefined {
  if (!instance) return undefined;
  return { type: instance.type, n: instance.n, field: instance.field, op: instance.op, value: instance.value, aggregate: instance.aggregate };
}

export function applyMappingSummaryDocument(doc: MappingSummaryDocument, destType: MappingDestType): AppliedMappingSummary {
  const mappingRows: MappingRow[] = [];
  const targetByResource: Record<string, string> = {};
  const extraTablesByGroup: Record<string, string[]> = {};
  const sqlTablesByName = new Map<string, DestinationTable>();
  const childTableRelationsByTable: Record<string, ChildTableRelation> = {};

  // A column's referenceLookup only carries a bare table name (the wire shape's cross-resource contract —
  // see computeResourceKeyInfo) — recovering which RESOURCE that table belongs to needs every entry's own
  // root table known up front, hence this separate pass before the main one below.
  const tableToResource: Record<string, string> = {};
  for (const entry of doc.mappings) {
    const rootTable = resolvePrimaryTable(entry.tables);
    if (rootTable) {
      tableToResource[rootTable.name] = entry.resourceType;
    }
  }

  for (const entry of doc.mappings) {
    const resource = entry.resourceType;
    const siblingNames = entry.tables.map(t => t.name);
    const fullNames = entry.tables.map(t => qualifyTableName(t.name, destType));
    const primaryIndex = Math.max(0, entry.tables.indexOf(resolvePrimaryTable(entry.tables) ?? entry.tables[0]));
    targetByResource[resource] = fullNames[primaryIndex] ?? fullNames[0] ?? '';
    extraTablesByGroup[resource] = fullNames.filter((_, i) => i !== primaryIndex);

    entry.tables.forEach((table, i) => {
      const fullName = fullNames[i];
      if (isGenuineChildRelation(table.relation, siblingNames)) {
        childTableRelationsByTable[fullName] = {
          parentTable: qualifyTableName(table.relation!.parentTable, destType),
          parentColumn: table.relation!.parentColumn,
          foreignKeyColumnName: table.relation!.childColumn,
        };
      }

      const schemaDef = entry.schemaChanges.tablesToCreate.find(t => t.name === table.name);
      const columnsAdded = new Set(entry.schemaChanges.columnsToAdd.filter(c => c.table === table.name).map(c => c.name));
      const existingTable = sqlTablesByName.get(fullName);
      const columnNames = new Set(existingTable?.columns.map(c => c.name) ?? []);
      const columns: DestinationColumn[] = existingTable?.columns ?? [];

      const wantedColumns = schemaDef
        ? schemaDef.columns.map(c => ({ name: c.name, dataType: c.dataType, mappingValueType: 'string', isNullable: true, maxLength: null }))
        : table.columns.map(c => ({
            name: c.column,
            dataType: isRelational(destType) ? DEFAULT_SQL_TYPE : DEFAULT_CSV_TYPE,
            mappingValueType: 'string', isNullable: true, maxLength: null,
            origin: columnsAdded.has(c.column) ? 'userCreated' as const : undefined,
          }));

      for (const c of wantedColumns) {
        if (!columnNames.has(c.name)) { columnNames.add(c.name); columns.push(c); }
      }

      sqlTablesByName.set(fullName, {
        schemaName: fullName.includes('.') ? fullName.slice(0, fullName.indexOf('.')) : 'dbo',
        tableName: table.name,
        fullName,
        columns,
        origin: table.isNew ? 'userCreated' : existingTable?.origin,
      });

      for (const col of table.columns) {
        const instance = instanceFromSummary(col.instance);
        if (col.mode === 'wholeNodeAsJson') {
          mappingRows.push({
            resource, sources: [], mode: 'childJson', childNodeId: col.sourceNode ?? '',
            instance, targetName: col.column, tableName: fullName,
            ...(col.isUpsertKey ? { isUpsertKey: true } : {}),
          });
          continue;
        }
        const arrayContext = col.instance?.arrayContext;
        const referencesResource = col.referenceLookup ? tableToResource[col.referenceLookup.table] : undefined;
        mappingRows.push({
          resource,
          sources: (col.sources ?? []).map(p => sourceRefFromPath(p, arrayContext, resource)),
          mode: 'value',
          delimiter: col.mode === 'joinedFields' ? col.delimiter : undefined,
          instance,
          targetName: col.column, tableName: fullName,
          ...(referencesResource ? { referencesResource } : {}),
          ...(col.isUpsertKey ? { isUpsertKey: true } : {}),
        });
      }
    });
  }

  return {
    mappingRows,
    targetByResource,
    extraTablesByGroup,
    // Non-relational destinations (CSV, Mongo, Medplum, FHIR) never have a real, probed schema — sqlTables
    // must stay empty for them exactly like a fresh wizard session (see isRelational's own doc comment).
    // Populating it here from the saved document's tables was actively harmful for CSV specifically: every
    // CSV resource shares the same generic table name ("csv"), so restoring a document that only had Patient
    // mapped seeded a "csv" table entry with just Patient's columns — then adding Encounter and saving made
    // pruneOrphanedMappingRows compare Encounter's brand-new columns against that stale, wrong-resource
    // column list and silently drop all of them as "orphaned" (the "Encounter gets Patient's data" bug).
    sqlTables: isRelational(destType) ? Array.from(sqlTablesByName.values()) : [],
    childTableRelationsByTable,
    selectedResources: doc.mappings.map(e => e.resourceType),
  };
}
