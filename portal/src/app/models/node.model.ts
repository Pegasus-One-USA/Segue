export type NodeKind = 'source' | 'transform' | 'merge';

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
  /** SOURCES catalog id (e.g. 'athena', 'cerner') for THIS node's real EHR vendor — independent of
   *  whatever backend workflow NodeType the node round-trips through. Every EHR vendor except Sample/
   *  GenericFhir currently saves under the generic 'EpicSourceNode' NodeType (no dedicated backend node
   *  type exists yet for Cerner/Athenahealth/Allscripts/Healow/Meditech — see
   *  workflow-graph-mapper.service.ts's transformIdForNode), so re-deriving the vendor from that on
   *  reload always guesses 'epic'. Persisted separately (workflow-graph-mapper.service.ts's `__vendorId`
   *  config key) purely for cosmetic display (abbr/color on the canvas) — never used for anything the
   *  backend validates. */
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

export type CanvasNode = SourceNode | TransformNode | MergeNode;

export function isSourceNode(n: CanvasNode): n is SourceNode {
  return !n.kind;
}

export function isTransformNode(n: CanvasNode): n is TransformNode {
  return n.kind === 'transform';
}

export function isMergeNode(n: CanvasNode): n is MergeNode {
  return n.kind === 'merge';
}
