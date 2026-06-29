export interface PayloadSourceEntry {
  ref: number;
  stage: 'source';
  connector: string;
  connected: boolean;
  config: Record<string, string>;
}

export interface PayloadTransformEntry {
  ref: number;
  stage: 'transform';
  rank: number;
  group: string | null;
  transform: string;
  appliedFrom: string | undefined;
  applicability: string | undefined;
}

export interface PayloadMergeEntry {
  ref: number;
  stage: 'merge';
  group: string;
  rank: number;
  mergesInputs: string[];
}

export type PayloadEntry = PayloadSourceEntry | PayloadTransformEntry | PayloadMergeEntry;

export interface PipelinePayload {
  scenario: string;
  tenant: string;
  nodes: PayloadEntry[];
  edges: { from: string; to: string }[];
}
