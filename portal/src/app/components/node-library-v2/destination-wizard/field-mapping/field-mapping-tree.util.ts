import type { ResourceFieldDef } from '../destination-wizard.component';

// ── Source-tree building ──────────────────────────────────────────────────────
// Turns the flat per-resource field catalog (ResourceFieldDef[], sourced from either the real
// backend FHIR catalog or the built-in fallback defs) into a nested tree so the mapping canvas can
// render accordion groups (e.g. Patient > Name > Given) instead of one flat dropdown.

export interface FmTreeLeafField {
  fhirPath: string;
  jsonPath?: string;
  valueType?: string;
  arrays?: string[];
  /** Resource types this field may reference (e.g. ["Patient"] for Observation.subject) — see
   *  ResourceFieldDef.referenceTargetTypes. */
  referenceTargetTypes?: string[];
}

export interface FmTreeNode {
  /** Stable key: "{resource}" for the root, "{resource}.{relativePath}" otherwise. */
  id: string;
  label: string;
  resource: string;
  kind: 'group' | 'leaf';
  /** Group only — true when this segment repeats (per the catalog's array-ancestor metadata). */
  isArray: boolean;
  children: FmTreeNode[];
  /** Group only — relative dot-path from the resource root; becomes MappingRow.childNodeId when this group is dropped whole. */
  groupPath?: string;
  /** Leaf only. */
  field?: FmTreeLeafField;
}

function titleCase(segment: string): string {
  return segment.length ? segment.charAt(0).toUpperCase() + segment.slice(1) : segment;
}

/**
 * Builds one resource's tree. Fields sourced from the real backend catalog (jsonPath present) nest by
 * dot-separated relative path; fields from the built-in fallback (DEST_RESOURCE_DEFS/genericResourceDef,
 * no jsonPath yet) render as flat single-level leaves directly under the root, matching today's
 * degraded UI until the real catalog loads.
 */
export function buildResourceTree(resource: string, fields: ResourceFieldDef[]): FmTreeNode {
  const root: FmTreeNode = {
    id: resource, label: resource, resource, kind: 'group', isArray: false, children: [], groupPath: '',
  };
  if (!fields.length) return root;

  const isFallback = fields.every(f => f.jsonPath === undefined);
  const groupIndex = new Map<string, FmTreeNode>([['', root]]);

  for (const f of fields) {
    const relPath = f.path.startsWith(`${resource}.`) ? f.path.slice(resource.length + 1) : f.path;
    const segments = isFallback ? [relPath] : relPath.split('.');

    let parent = root;
    let cum = '';
    for (let i = 0; i < segments.length - 1; i++) {
      cum = cum ? `${cum}.${segments[i]}` : segments[i];
      let group = groupIndex.get(cum);
      if (!group) {
        group = {
          id: `${resource}.${cum}`,
          label: titleCase(segments[i]),
          resource,
          kind: 'group',
          isArray: (f.arrays ?? []).includes(cum),
          children: [],
          groupPath: cum,
        };
        groupIndex.set(cum, group);
        parent.children.push(group);
      }
      parent = group;
    }

    parent.children.push({
      id: `${resource}.${relPath}`,
      label: f.label,
      resource,
      kind: 'leaf',
      isArray: false,
      children: [],
      field: {
        fhirPath: f.path, jsonPath: f.jsonPath, valueType: f.valueType, arrays: f.arrays,
        referenceTargetTypes: f.referenceTargetTypes,
      },
    });
  }

  return root;
}

/** Multiple selected resources become sibling roots, in the order they were selected. */
export function buildForest(
  resources: string[],
  availableFields: (r: string) => ResourceFieldDef[],
): FmTreeNode[] {
  return resources.map(r => buildResourceTree(r, availableFields(r)));
}

/**
 * Every string a node can be found by, lowercased once up front. Covers name/path, data type, and the
 * "array" badge already shown in the UI — never a guessed type-label mapping, only what the field's own
 * metadata (or the group's own array-ancestor flag) actually carries:
 *  - leaf: display label, full resource-qualified fhirPath (so "meta.security" finds it even though
 *    that dotted path isn't in the label), the real valueType off ResourceFieldDef/FhirElement (String,
 *    Integer, Decimal, Boolean, Date, DateTime, Json, ... whatever the catalog sends — never hardcoded
 *    to a fixed int/string list), any referenceTargetTypes (e.g. "patient" finds Observation's "Subject
 *    › Reference"), and the literal word "array" when this leaf sits under a repeating ancestor.
 *  - group: display label, its own relative groupPath, its full id (resource-qualified path), and
 *    "array" when this group segment itself repeats (isArray) — same word as its on-screen badge.
 */
function nodeHaystack(node: FmTreeNode): string[] {
  if (node.kind === 'leaf') {
    const f = node.field;
    return [
      node.label,
      f?.fhirPath ?? '',
      f?.valueType ?? '',
      ...(f?.referenceTargetTypes ?? []),
      ...(f?.arrays?.length ? ['array'] : []),
    ].map(s => s.toLowerCase());
  }
  return [
    node.label,
    node.groupPath ?? '',
    node.id,
    ...(node.isArray ? ['array'] : []),
  ].map(s => s.toLowerCase());
}

/** Splits a raw query into whitespace-separated, trimmed, lowercased terms — shared by every
 *  field-mapping search box (the payload tree here, and destination table columns in
 *  field-mapping-target-card.component.ts) so "multiple terms AND together, each term OR's across a
 *  candidate's own searchable strings" means exactly the same thing everywhere in this feature. */
export function searchTerms(query: string): string[] {
  return query.trim().toLowerCase().split(/\s+/).filter(Boolean);
}

/** True when every term matches SOMETHING in `haystack` (terms may each match a different entry — e.g.
 *  "patient string" matches via a path entry for "patient" and a type entry for "string" on the same
 *  candidate). AND across terms, OR across haystack entries per term. Lowercases defensively, so callers
 *  may pass raw-case strings. */
export function matchesSearchTerms(haystack: string[], terms: string[]): boolean {
  if (!terms.length) return true;
  const lower = haystack.map(h => h.toLowerCase());
  return terms.every(t => lower.some(h => h.includes(t)));
}

/**
 * Filters a tree by a whitespace-separated, case-insensitive, partial-match term list (see
 * matchesSearchTerms/nodeHaystack for what a term can match against: name, full path, data type,
 * reference target, or the "array" badge). If a GROUP's own text satisfies every term, its entire
 * subtree is kept as-is — searching "meta" or "address" should surface the whole hierarchy, not just
 * the children whose own label also happens to repeat the word. Otherwise each child is filtered
 * independently and pruned parents are dropped, so a deep single-leaf match (e.g. "code" finding only
 * Meta › Security › Code) still renders with its full parent chain intact rather than flattened.
 */
export function filterTree(node: FmTreeNode, query: string): FmTreeNode | null {
  const terms = searchTerms(query);
  if (!terms.length) return node;
  return filterByTerms(node, terms);
}

function filterByTerms(node: FmTreeNode, terms: string[]): FmTreeNode | null {
  if (node.kind === 'leaf') return matchesSearchTerms(nodeHaystack(node), terms) ? node : null;
  if (matchesSearchTerms(nodeHaystack(node), terms)) return node;

  const children = node.children
    .map(child => filterByTerms(child, terms))
    .filter((child): child is FmTreeNode => child !== null);
  return children.length ? { ...node, children } : null;
}

/** Forest-level filterTree — an empty/blank query returns the forest unchanged. */
export function filterForest(forest: FmTreeNode[], query: string): FmTreeNode[] {
  if (!query.trim()) return forest;
  return forest
    .map(root => filterTree(root, query))
    .filter((root): root is FmTreeNode => root !== null);
}

/** Flattens every leaf under a node (used by auto-map and by the keyboard "+ Add mapping" field picker). */
export function flattenLeaves(node: FmTreeNode): FmTreeNode[] {
  if (node.kind === 'leaf') return [node];
  return node.children.flatMap(flattenLeaves);
}

/** Finds a node by id anywhere in the forest (used to resolve a drag/keyboard-armed source by id). */
export function findNode(forest: FmTreeNode[], id: string): FmTreeNode | null {
  for (const root of forest) {
    const hit = findInSubtree(root, id);
    if (hit) return hit;
  }
  return null;
}

function findInSubtree(node: FmTreeNode, id: string): FmTreeNode | null {
  if (node.id === id) return node;
  for (const child of node.children) {
    const hit = findInSubtree(child, id);
    if (hit) return hit;
  }
  return null;
}
