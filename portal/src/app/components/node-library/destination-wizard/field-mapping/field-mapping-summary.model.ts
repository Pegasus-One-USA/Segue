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
import { MappingRow, MappingInstanceSelection, MappingSourceRef } from './field-mapping-model';
import { FmTreeNode, buildForest, findNode } from './field-mapping-tree.util';
import { dependencyRankFor } from '../resource-dependency.config';

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
}

export interface MappingSummaryTable {
  name: string;
  isNew: boolean;
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
  destination: { type: 'sql' | 'csv'; label: string };
  tables: MappingSummaryTable[];
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

function qualify(name: string, destType: 'sql' | 'csv'): string {
  if (destType !== 'sql' || name.includes('.')) return name;
  return `dbo.${name}`;
}

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

function toSummaryColumn(row: MappingRow, forest: FmTreeNode[]): MappingSummaryColumn {
  const instance = toSummaryInstance(row, arrayContextFor(row, forest));
  if (row.mode === 'childJson') {
    return { column: row.targetName, mode: 'wholeNodeAsJson', sourceNode: row.childNodeId ?? '', instance };
  }
  const sources = row.sources.map(s => s.fhirPath);
  if (sources.length > 1) {
    return { column: row.targetName, mode: 'joinedFields', sources, delimiter: row.delimiter ?? ', ', instance };
  }
  return { column: row.targetName, mode: 'directField', sources, instance };
}

// ── per-resource table set + schema-change/processing-order derivation ──────────────────────────────

interface ResolvedTable {
  fullName: string;
  bare: string;
  isNew: boolean;
  relation: ChildTableRelation | undefined;
  columns: DestinationColumn[];
}

function resolveTable(
  fullName: string,
  sqlTables: DestinationTable[],
  childTableRelationsByTable: Record<string, ChildTableRelation>,
  destType: 'sql' | 'csv',
): ResolvedTable {
  const known = sqlTables.find(t => t.fullName === fullName);
  return {
    fullName,
    bare: bareName(fullName),
    isNew: known ? known.origin === 'userCreated' : true,
    relation: childTableRelationsByTable[fullName],
    columns: known?.columns ?? [{
      name: 'Id',
      dataType: destType === 'sql' ? DEFAULT_ID_TYPE : DEFAULT_CSV_TYPE,
      mappingValueType: 'string', isNullable: false, maxLength: null,
    }],
  };
}

function buildSchemaChanges(tables: ResolvedTable[], destType: 'sql' | 'csv'): MappingSchemaChanges {
  const fallbackType = destType === 'sql' ? DEFAULT_SQL_TYPE : DEFAULT_CSV_TYPE;

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

// ── build ─────────────────────────────────────────────────────────────────────────────────────────

export interface BuildMappingSummaryParams {
  sourceVendor: string;
  destType: 'sql' | 'csv';
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
}

export function buildMappingSummaryDocument(params: BuildMappingSummaryParams): MappingSummaryDocument {
  const {
    sourceVendor, destType, destLabel, mappingRows, sqlTables, childTableRelationsByTable, availableFields,
    sourceConnectionId, destinationId,
  } = params;
  const generatedAt = new Date().toISOString();

  // Rank order, not first-appearance order — a prerequisite resource (Patient) always sorts before
  // whatever requires it, matching processingOrder's own table-level ordering one level up.
  const resources = Array.from(new Set(mappingRows.map(r => r.resource)))
    .sort((a, b) => dependencyRankFor(a) - dependencyRankFor(b));

  const mappings: MappingResourceEntry[] = resources.map(resource => {
    const resourceRows = mappingRows.filter(r => r.resource === resource);
    const tableNames = Array.from(new Set(resourceRows.map(r => r.tableName)));
    const resolved = tableNames.map(name => resolveTable(name, sqlTables, childTableRelationsByTable, destType));
    const forest = buildForest([resource], availableFields);

    const tables: MappingSummaryTable[] = resolved.map(t => ({
      name: t.bare,
      isNew: t.isNew,
      relation: t.relation
        ? { childColumn: t.relation.foreignKeyColumnName, parentTable: bareName(t.relation.parentTable), parentColumn: t.relation.parentColumn }
        : null,
      columns: resourceRows.filter(r => r.tableName === t.fullName).map(r => toSummaryColumn(r, forest)),
    }));

    return {
      resourceType: resource,
      rank: dependencyRankFor(resource),
      generatedAt,
      schemaChanges: buildSchemaChanges(resolved, destType),
      processingOrder: computeProcessingOrder(resolved),
      destination: { type: destType, label: destLabel },
      tables,
    };
  });

  return {
    source: sourceVendor,
    destination: destType === 'sql' ? 'SQL' : 'CSV',
    sourceConnectionId,
    destinationId,
    mappings,
  };
}

// ── apply (reverse) — reconstructs wizard state from a previously saved document. jsonPath/valueType
// and the full array-ancestor chain aren't part of this schema, so a reloaded-only row only gets its
// immediate arrayContext back, not the richer catalog metadata a live-mapped row would have — accepted,
// since that data has no place in this contract. ──

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

export function applyMappingSummaryDocument(doc: MappingSummaryDocument, destType: 'sql' | 'csv'): AppliedMappingSummary {
  const mappingRows: MappingRow[] = [];
  const targetByResource: Record<string, string> = {};
  const extraTablesByGroup: Record<string, string[]> = {};
  const sqlTablesByName = new Map<string, DestinationTable>();
  const childTableRelationsByTable: Record<string, ChildTableRelation> = {};

  for (const entry of doc.mappings) {
    const resource = entry.resourceType;
    const fullNames = entry.tables.map(t => qualify(t.name, destType));
    const primaryIndex = Math.max(0, entry.tables.findIndex(t => !t.relation));
    targetByResource[resource] = fullNames[primaryIndex] ?? fullNames[0] ?? '';
    extraTablesByGroup[resource] = fullNames.filter((_, i) => i !== primaryIndex);

    entry.tables.forEach((table, i) => {
      const fullName = fullNames[i];
      if (table.relation) {
        childTableRelationsByTable[fullName] = {
          parentTable: qualify(table.relation.parentTable, destType),
          parentColumn: table.relation.parentColumn,
          foreignKeyColumnName: table.relation.childColumn,
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
            dataType: destType === 'sql' ? DEFAULT_SQL_TYPE : DEFAULT_CSV_TYPE,
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
          });
          continue;
        }
        const arrayContext = col.instance?.arrayContext;
        mappingRows.push({
          resource,
          sources: (col.sources ?? []).map(p => sourceRefFromPath(p, arrayContext, resource)),
          mode: 'value',
          delimiter: col.mode === 'joinedFields' ? col.delimiter : undefined,
          instance,
          targetName: col.column, tableName: fullName,
        });
      }
    });
  }

  return {
    mappingRows,
    targetByResource,
    extraTablesByGroup,
    sqlTables: Array.from(sqlTablesByName.values()),
    childTableRelationsByTable,
    selectedResources: doc.mappings.map(e => e.resourceType),
  };
}
