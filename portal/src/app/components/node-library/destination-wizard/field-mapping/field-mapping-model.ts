// ── Field-mapping data model ─────────────────────────────────────────────────
// Extends the wizard's original single-source MappingRow with joins (multiple
// source fields into one column) and array "instance selection" (all/first/
// nth/criteria), while keeping the legacy flat dest_mappings wire format alive
// so workflow-build-assembler.service.ts keeps working unmodified.

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

/**
 * Flattens the rich MappingRow[] back to the legacy wire shape for dest_mappings, using each row's
 * primary (first) source. Callers that also want the full shape for round-tripping the wizard's own
 * UI state should separately persist JSON.stringify(rows) under dest_mappings_v2.
 */
export function serializeRowsFlat(
  rows: MappingRow[],
  targetByResource: Record<string, string>,
): (LegacyMappingRow & { arrayPolicy: string; approximated: boolean })[] {
  return rows.map(row => {
    const primary = row.sources[0];
    const { arrayPolicy, approximated } = resolveArrayPolicy(row);
    return {
      resource: row.resource,
      field: primary?.label ?? '',
      path: primary?.fhirPath ?? row.childNodeId ?? '',
      target: targetByResource[row.resource] ?? row.tableName,
      column: row.targetName,
      jsonPath: primary?.jsonPath,
      valueType: arrayPolicy === 'StoreJson' ? 'Json' : primary?.valueType,
      arrays: primary?.arrays,
      arrayPolicy,
      approximated,
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
