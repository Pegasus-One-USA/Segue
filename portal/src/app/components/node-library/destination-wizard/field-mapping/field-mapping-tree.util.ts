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
      field: { fhirPath: f.path, jsonPath: f.jsonPath, valueType: f.valueType, arrays: f.arrays },
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
