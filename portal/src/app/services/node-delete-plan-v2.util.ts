import { CanvasNode, TransformNode, isSourceNode, isTransformNode } from '../models/node-v2.model';
import { TRANSFORMS } from '../data/transforms-v2.data';

/** Minimal edge shape — the store's CanvasEdge, narrowed so this file stays dependency-free. */
export interface PlanEdge {
  id: string;
  from: string;
  to: string;
}

/** V2's chain steps in canonical canvas order, Source → … → Destination.
 *  Mirrors ApplicabilityServiceV2.CHAIN_STEP_IDS / the builder's CHAIN_STEP_IDS. */
const CHAIN_STEP_IDS: readonly string[] = ['field-mapping', 'transformation', 'deidentification'];

/**
 * The destination-node field keys each chain step is the sole author of.
 *
 * A chain node carries no configuration of its own — the destination hosts it (see
 * NodeLibraryDialogComponent.onDestWizardSaved, which emits a chain edit against the DESTINATION's
 * node id). So removing the node is only half of removing the step: without stripping these keys the
 * data stays on the canvas, the graph mapper still injects a synthetic Mapping node from it
 * (workflow-graph-mapper-v2.service.ts's `${to.id}__mapping` branch), and syncChainNodes puts the node
 * straight back on the next destination save.
 *
 * `transformation` owns no key here on purpose: transformation rules are the one step that lives
 * server-side (TransformationRule rows), so there is nothing local to clear — see the caveat on
 * NodeDeletePlan.serverSideLeftovers.
 */
const CHAIN_STEP_OWNED_FIELDS: Record<string, readonly string[]> = {
  'field-mapping': [
    'dest_mappings',
    'dest_mappings_v2',
    'dest_mapping_summary_v1',
    'dest_mappingCount',
    'dest_targets',
    'dest_sourcePayloadFields',
    // Server-assigned ids for the profiles those rows were saved as. Left behind, the next Save would
    // update the very MappingProfile records this delete was meant to walk away from.
    'mappingProfileId',
    'mappingProfileIds',
  ],
  transformation: [],
  deidentification: ['deIdentificationProfileId'],
};

/** One node the plan will remove, with what to show for it in the confirmation. */
export interface PlannedNodeRemoval {
  id: string;
  label: string;
  /** Secondary line — the configured connection/row count, when there is one. */
  detail: string | null;
}

/** Configuration to strip off a SURVIVING node because the step that authored it is being removed. */
export interface PlannedFieldClear {
  /** The destination node that hosts the data (never itself part of the removal). */
  nodeId: string;
  keys: string[];
  /** Confirmation copy, e.g. "12 mapped fields (held on the SQL Server module)". */
  label: string;
}

export interface NodeDeletePlan {
  /** The node the user actually asked to delete. */
  targetId: string;
  targetLabel: string;
  /** Every node removed, upstream-first. Always includes targetId. */
  removals: PlannedNodeRemoval[];
  /** Field keys to strip from surviving nodes so no orphaned configuration is left behind. */
  fieldClears: PlannedFieldClear[];
  /** Edges to add after the removal so the surviving graph stays one connected chain. */
  relink: { from: string; to: string }[];
  /** Steps whose data does NOT live on the canvas and therefore survives this delete — shown in the
   *  confirmation so the wording never over-promises. Currently only ever "transformation rules". */
  serverSideLeftovers: string[];
}

function isDestinationNode(node: CanvasNode): boolean {
  return isTransformNode(node) && node.transformId.startsWith('dest-');
}

function isChainStep(node: CanvasNode): boolean {
  return isTransformNode(node) && CHAIN_STEP_IDS.includes(node.transformId);
}

function transformIdOf(node: CanvasNode): string | null {
  return isTransformNode(node) ? (node as TransformNode).transformId : null;
}

/** Every node reachable downstream of `startIds`, including the starts themselves. */
function forwardClosure(startIds: string[], edges: PlanEdge[]): Set<string> {
  const seen = new Set<string>();
  const stack = [...startIds];
  while (stack.length) {
    const id = stack.pop()!;
    if (!seen.add(id)) continue;
    for (const edge of edges) {
      if (edge.from === id && !seen.has(edge.to)) stack.push(edge.to);
    }
  }
  return seen;
}

/** The chain segment sitting immediately upstream of `destination`, in canvas order. */
function chainStepsBefore(
  destination: CanvasNode,
  nodes: CanvasNode[],
  edges: PlanEdge[],
): CanvasNode[] {
  const chain: CanvasNode[] = [];
  let cursor: CanvasNode | undefined = destination;
  let guard = 0;
  while (cursor && guard++ < 20) {
    const inbound = edges.find(edge => edge.to === cursor!.id);
    const predecessor = inbound ? nodes.find(node => node.id === inbound.from) : undefined;
    if (!predecessor || !isChainStep(predecessor)) break;
    chain.unshift(predecessor);
    cursor = predecessor;
  }
  return chain;
}

/** The chain steps downstream of `step` in its own segment — stops at the destination. */
function chainStepsAfter(step: CanvasNode, nodes: CanvasNode[], edges: PlanEdge[]): CanvasNode[] {
  const rest: CanvasNode[] = [];
  let cursor: CanvasNode | undefined = step;
  let guard = 0;
  while (cursor && guard++ < 20) {
    const outbound = edges.find(edge => edge.from === cursor!.id);
    const successor = outbound ? nodes.find(node => node.id === outbound.to) : undefined;
    if (!successor || !isChainStep(successor)) break;
    rest.push(successor);
    cursor = successor;
  }
  return rest;
}

/** The destination that terminates `node`'s pipeline (a destination resolves to itself). */
function destinationFor(node: CanvasNode, nodes: CanvasNode[], edges: PlanEdge[]): CanvasNode | null {
  let cursor: CanvasNode | undefined = node;
  let guard = 0;
  while (cursor && guard++ < 20) {
    if (isDestinationNode(cursor)) return cursor;
    const outbound = edges.find(edge => edge.from === cursor!.id);
    cursor = outbound ? nodes.find(candidate => candidate.id === outbound.to) : undefined;
  }
  return null;
}

function labelOf(node: CanvasNode): string {
  const named = node.fields?.['__name'];
  if (named) return named;
  const transformId = transformIdOf(node);
  if (transformId) return TRANSFORMS.find(t => t.id === transformId)?.name ?? 'Module';
  if (node.kind === 'merge') return 'Merge';
  return 'Source';
}

function mappingRowCount(fields: Record<string, string>): number {
  const explicit = Number(fields['dest_mappingCount']);
  if (Number.isFinite(explicit) && explicit > 0) return explicit;
  for (const key of ['dest_mappings_v2', 'dest_mappings']) {
    try {
      const rows = JSON.parse(fields[key] ?? '[]') as unknown[];
      if (Array.isArray(rows) && rows.length) return rows.length;
    } catch { /* malformed config just means "no count to show" */ }
  }
  return 0;
}

/** `ownerFields` is the hosting destination's field bag for a chain step — that's where a chain step's
 *  configuration actually lives, so a Mapping node's own fields have no row count to report. */
function detailOf(node: CanvasNode, ownerFields: Record<string, string>): string | null {
  const fields = node.fields ?? {};
  if (isDestinationNode(node)) return fields['dest_name'] || null;
  switch (transformIdOf(node)) {
    case 'field-mapping': {
      const count = mappingRowCount(ownerFields);
      return count ? `${count} mapped field${count === 1 ? '' : 's'}` : null;
    }
    case 'deidentification':
      return 'De-identification policy';
    case 'transformation':
      return 'Transformation rules';
    default:
      return null;
  }
}

/**
 * What deleting `nodeId` actually entails — the full removal set, the configuration that has to be
 * stripped with it, and the edges needed to keep the survivors connected. Returns null for an unknown
 * node id.
 *
 * The cascade rules, all of them following the dependency direction the canvas already expresses
 * (Source → Mapping → Transformation → De-identification → Destination):
 *
 *  - **Source** — takes its whole pipeline. Anything still reachable from another source is kept, so
 *    deleting one leg of a multi-source merge doesn't take the other leg's nodes with it.
 *  - **Destination** — takes its chain segment (Mapping / Transformation / De-identification). Those
 *    steps exist only to feed it and store their configuration on it, so they cannot outlive it.
 *  - **Mapping** — takes Transformation and De-identification, which run on mapped records. The
 *    destination survives; the source is relinked straight to it.
 *  - **Transformation / De-identification** — itself only. Its neighbours are spliced back together.
 *  - **Anything else** (a plain transform, a merge) — itself only, as before.
 */
export function buildNodeDeletePlanV2(
  nodeId: string,
  nodes: CanvasNode[],
  edges: PlanEdge[],
): NodeDeletePlan | null {
  const target = nodes.find(node => node.id === nodeId);
  if (!target) return null;

  const removedIds = new Set<string>([target.id]);

  if (isSourceNode(target)) {
    for (const id of forwardClosure([target.id], edges)) removedIds.add(id);
    // Keep whatever another source still feeds — its pipeline is not this delete's business.
    const survivingSourceIds = nodes
      .filter(node => isSourceNode(node) && node.id !== target.id)
      .map(node => node.id);
    if (survivingSourceIds.length) {
      for (const keptId of forwardClosure(survivingSourceIds, edges)) {
        if (keptId !== target.id) removedIds.delete(keptId);
      }
    }
  } else if (isDestinationNode(target)) {
    for (const step of chainStepsBefore(target, nodes, edges)) removedIds.add(step.id);
  } else if (transformIdOf(target) === 'field-mapping') {
    for (const step of chainStepsAfter(target, nodes, edges)) removedIds.add(step.id);
  }

  // Splice the removal out of the chain: every surviving predecessor reconnects to every surviving
  // successor. Deleting a Mapping node this way relinks the source straight to its destination;
  // deleting a whole pipeline (source) or a terminal destination has nothing to reconnect.
  const relink: { from: string; to: string }[] = [];
  const externalIn = [...new Set(
    edges.filter(edge => removedIds.has(edge.to) && !removedIds.has(edge.from)).map(edge => edge.from),
  )];
  const externalOut = [...new Set(
    edges.filter(edge => removedIds.has(edge.from) && !removedIds.has(edge.to)).map(edge => edge.to),
  )];
  for (const from of externalIn) {
    for (const to of externalOut) {
      if (from === to) continue;
      if (edges.some(edge => edge.from === from && edge.to === to)) continue;
      relink.push({ from, to });
    }
  }

  // Configuration a removed chain step authored on a destination that is NOT going away with it.
  const clearsByNode = new Map<string, { keys: Set<string>; labels: string[] }>();
  const serverSideLeftovers: string[] = [];
  for (const removedId of removedIds) {
    const node = nodes.find(candidate => candidate.id === removedId);
    if (!node || !isChainStep(node)) continue;
    const transformId = transformIdOf(node)!;
    const owner = destinationFor(node, nodes, edges);

    if (transformId === 'transformation' && owner && !removedIds.has(owner.id)) {
      serverSideLeftovers.push(
        'Transformation rules are stored server-side, so they stay until deleted from the destination’s Rules screen.',
      );
    }
    if (!owner || removedIds.has(owner.id)) continue;

    const ownerFields = owner.fields ?? {};
    const present = (CHAIN_STEP_OWNED_FIELDS[transformId] ?? []).filter(
      key => ownerFields[key] !== undefined && ownerFields[key] !== '',
    );
    if (!present.length) continue;

    const entry = clearsByNode.get(owner.id) ?? { keys: new Set<string>(), labels: [] };
    present.forEach(key => entry.keys.add(key));
    const ownerLabel = labelOf(owner);
    if (transformId === 'field-mapping') {
      const count = mappingRowCount(ownerFields);
      entry.labels.push(
        `${count ? `${count} mapped field${count === 1 ? '' : 's'}` : 'Field mappings'} and target tables (held on ${ownerLabel})`,
      );
    } else {
      entry.labels.push(`De-identification profile selection (held on ${ownerLabel})`);
    }
    clearsByNode.set(owner.id, entry);
  }

  const fieldClears: PlannedFieldClear[] = [...clearsByNode.entries()].map(([id, entry]) => ({
    nodeId: id,
    keys: [...entry.keys],
    label: entry.labels.join('; '),
  }));

  // Upstream-first, so the confirmation reads in the same direction as the canvas.
  const order = new Map(nodes.map((node, index) => [node.id, index]));
  const removals: PlannedNodeRemoval[] = [...removedIds]
    .map(id => nodes.find(node => node.id === id))
    .filter((node): node is CanvasNode => !!node)
    .sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0))
    .map(node => ({
      id: node.id,
      label: labelOf(node),
      detail: detailOf(node, destinationFor(node, nodes, edges)?.fields ?? {}),
    }));

  return {
    targetId: target.id,
    targetLabel: labelOf(target),
    removals,
    fieldClears,
    relink,
    serverSideLeftovers: [...new Set(serverSideLeftovers)],
  };
}
