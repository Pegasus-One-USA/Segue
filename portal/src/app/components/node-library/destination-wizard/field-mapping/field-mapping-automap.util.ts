import { MappingRow } from './field-mapping-model';
import { FmTreeNode, flattenLeaves } from './field-mapping-tree.util';

/** Lowercases and strips everything but letters/digits, so "First Name"/"first_name"/"FirstName" all collide. */
function normalize(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]/g, '');
}

function lastSegment(fhirPath: string): string {
  const parts = fhirPath.split('.');
  return parts[parts.length - 1];
}

/**
 * Generic normalized-name matcher — deliberately NOT the mockup's hardcoded demo-specific synonym
 * table (that one only makes sense for its fictional Patient/Contact/Insurance schema). Tries, in
 * order: exact label match, exact last-path-segment match, then substring containment as a loose
 * last resort. Returns only NEW rows for columns that don't already have a mapping.
 */
export function autoMap(
  forest: FmTreeNode[],
  existingRows: MappingRow[],
  targetByResource: Record<string, string>,
  columnsFor: (resource: string) => string[],
): MappingRow[] {
  const added: MappingRow[] = [];

  for (const root of forest) {
    const resource = root.resource;
    const leaves = flattenLeaves(root);
    const alreadyMapped = new Set(
      existingRows.filter(r => r.resource === resource).map(r => r.targetName),
    );

    for (const column of columnsFor(resource)) {
      if (alreadyMapped.has(column)) continue;
      const nCol = normalize(column);
      if (!nCol) continue;

      const match =
        leaves.find(l => normalize(l.label) === nCol) ??
        leaves.find(l => l.field && normalize(lastSegment(l.field.fhirPath)) === nCol) ??
        leaves.find(l => nCol.includes(normalize(l.label)) || normalize(l.label).includes(nCol));

      if (!match || !match.field) continue;

      added.push({
        resource,
        sources: [{
          fhirPath: match.field.fhirPath,
          label: match.label,
          jsonPath: match.field.jsonPath,
          valueType: match.field.valueType,
          arrays: match.field.arrays,
        }],
        mode: 'value',
        instance: { type: 'first' },
        targetName: column,
        tableName: targetByResource[resource] ?? '',
      });
      alreadyMapped.add(column);
    }
  }

  return added;
}
