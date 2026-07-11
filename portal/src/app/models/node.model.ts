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
