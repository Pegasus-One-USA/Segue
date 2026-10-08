export type NodeKind = 'source' | 'transform' | 'merge' | 'mpi';

export interface BaseNode {
  id: string;
  x: number;
  y: number;
  kind?: NodeKind;
  fields: Record<string, string>;
  /** Opt-in per-node "Copy URL" checkpoint (Phase 1) — see docs/backend/05-workflow-node-checkpoints-plan.md. */
  checkpointUrlEnabled?: boolean;
}

export interface SourceNode extends BaseNode {
  kind?: undefined;
  connected: boolean;
  abbr?: string;
  color?: string;
  connectorLabel?: string;
  /** SOURCES catalog id (e.g. 'athena', 'cerner') for THIS node's real EHR vendor. Persisted as
   *  workflow-graph-mapper.service.ts's `__vendorId` config key, and read back on reload for the canvas
   *  abbr/color. It is ALSO what transformIdForNode now picks the node's backend NodeType from, so a vendor
   *  with a NodeType of its own (see VENDOR_SOURCE_TRANSFORM_IDS) saves as e.g. 'AthenahealthSourceNode'
   *  rather than sharing 'EpicSourceNode'. A vendor still gated out of the backend catalog
   *  (Cerner/Allscripts/Meditech), and any node saved before that change, keeps the shared 'EpicSourceNode' —
   *  which is exactly why the vendor is recorded here rather than re-derived from the NodeType on reload. */
  vendorId?: string;
}

export interface TransformNode extends BaseNode {
  kind: 'transform';
  transformId: string;
  sourceName?: string;
  statusAtAdd?: string;
}

export interface MergeNode extends BaseNode {
  kind: 'merge';
  group: string;
  sourceName?: string;
}

/**
 * The Master Patient Index step — optional, at most one per workflow, added from a source's `+` and placed
 * directly after it (Source → MPI → …). An ordinary graph node on the canvas, but bypassed in the graph a run
 * executes (see WorkflowGraphMapperServiceV2.bypassMpi). The identifiers it matches on live in
 * fields[MPI_IDENTIFIERS_FIELD].
 */
export interface MpiNode extends BaseNode {
  kind: 'mpi';
}

export type CanvasNode = SourceNode | TransformNode | MergeNode | MpiNode;

export function isSourceNode(n: CanvasNode): n is SourceNode {
  return !n.kind;
}

export function isTransformNode(n: CanvasNode): n is TransformNode {
  return n.kind === 'transform';
}

export function isMergeNode(n: CanvasNode): n is MergeNode {
  return n.kind === 'merge';
}

export function isMpiNode(n: CanvasNode): n is MpiNode {
  return n.kind === 'mpi';
}
