import { MappingRow, MappingSourceRef } from './field-mapping-model';
import { FmTreeNode, flattenLeaves } from './field-mapping-tree.util';

/** Lowercases and strips everything but letters/digits, so "First Name"/"first_name"/"FirstName" all collide. */
function normalize(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]/g, '');
}

function lastSegment(fhirPath: string): string {
  const parts = fhirPath.split('.');
  return parts[parts.length - 1];
}

// ── Confidence-scored suggestions (see suggestMappings below) ──────────────────
// >= AUTO_MAP_THRESHOLD maps automatically; between the two thresholds is offered as a dashed,
// click-to-accept suggestion wire; below SUGGEST_THRESHOLD is left unmapped entirely.
export const AUTO_MAP_THRESHOLD = 0.90;
export const SUGGEST_THRESHOLD = 0.70;

/** Classic edit-distance — same algorithm mapping-profile-form.component.ts's _nameSimilarity uses for
 *  its own (unrelated) Mapping Profile editor, reimplemented here rather than imported since that
 *  component lives in a different editor with its own flatter row shape (see project notes). */
function levenshtein(a: string, b: string): number {
  const m = a.length;
  const n = b.length;
  if (m === 0) return n;
  if (n === 0) return m;
  const dp = new Array<number>(n + 1);
  for (let j = 0; j <= n; j++) dp[j] = j;
  for (let i = 1; i <= m; i++) {
    let prevDiag = dp[0];
    dp[0] = i;
    for (let j = 1; j <= n; j++) {
      const temp = dp[j];
      dp[j] = a[i - 1] === b[j - 1] ? prevDiag : 1 + Math.min(prevDiag, dp[j], dp[j - 1]);
      prevDiag = temp;
    }
  }
  return dp[n];
}

/** 1.0 = identical once normalized, 0.0 = nothing in common — normalized edit distance ratio. */
function similarity(a: string, b: string): number {
  const na = normalize(a);
  const nb = normalize(b);
  if (!na || !nb) return 0;
  return 1 - levenshtein(na, nb) / Math.max(na.length, nb.length);
}

function bestLeafMatch(column: string, leaves: FmTreeNode[]): { leaf: FmTreeNode; score: number } | null {
  let best: { leaf: FmTreeNode; score: number } | null = null;
  for (const leaf of leaves) {
    if (!leaf.field) continue;
    const score = Math.max(similarity(column, leaf.label), similarity(column, lastSegment(leaf.field.fhirPath)));
    if (!best || score > best.score) best = { leaf, score };
  }
  return best;
}

function toSourceRef(leaf: FmTreeNode): MappingSourceRef {
  return {
    fhirPath: leaf.field!.fhirPath,
    label: leaf.label,
    jsonPath: leaf.field!.jsonPath,
    valueType: leaf.field!.valueType,
    arrays: leaf.field!.arrays,
  };
}

export interface MappingSuggestion {
  row: MappingRow;
  /** 0..1 similarity score that put this in the "needs review" band rather than auto-mapped. */
  confidence: number;
}

export interface SuggestMappingsResult {
  /** Columns that scored >= AUTO_MAP_THRESHOLD — added as real rows immediately, same as autoMap()'s
   *  return today (this function replaces autoMap as the canvas's own "Auto-map fields" action). */
  autoMapped: MappingRow[];
  /** Columns that scored between the two thresholds — rendered as dashed "does this look right?" wires
   *  the user must explicitly accept before they become real MappingRow entries. */
  suggestions: MappingSuggestion[];
}

/**
 * Scored version of autoMap() below — button-triggered only (see FieldMappingCanvasComponent's "Suggest
 * mappings" action), never run automatically on table/resource selection. Every so-far-unmapped column
 * is matched against every source leaf by normalized-name similarity (Levenshtein ratio against both the
 * leaf's display label and its FHIR path's last segment, whichever scores higher), then banded by
 * confidence: >=0.90 auto-maps, 0.70–0.90 becomes a reviewable suggestion, <0.70 is left unmapped.
 */
export function suggestMappings(
  forest: FmTreeNode[],
  existingRows: MappingRow[],
  targetByResource: Record<string, string>,
  columnsFor: (resource: string) => string[],
): SuggestMappingsResult {
  const autoMapped: MappingRow[] = [];
  const suggestions: MappingSuggestion[] = [];

  for (const root of forest) {
    const resource = root.resource;
    const leaves = flattenLeaves(root);
    const alreadyMapped = new Set(
      existingRows.filter(r => r.resource === resource).map(r => r.targetName),
    );

    for (const column of columnsFor(resource)) {
      if (alreadyMapped.has(column)) continue;
      const best = bestLeafMatch(column, leaves);
      if (!best || best.score < SUGGEST_THRESHOLD) continue;

      const row: MappingRow = {
        resource,
        sources: [toSourceRef(best.leaf)],
        mode: 'value',
        instance: { type: 'first' },
        targetName: column,
        tableName: targetByResource[resource] ?? '',
      };

      if (best.score >= AUTO_MAP_THRESHOLD) {
        autoMapped.push(row);
      } else {
        suggestions.push({ row, confidence: best.score });
      }
      alreadyMapped.add(column);
    }
  }

  return { autoMapped, suggestions };
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
