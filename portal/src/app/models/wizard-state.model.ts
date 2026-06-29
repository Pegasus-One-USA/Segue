export interface WizardState {
  step: 1 | 2 | 3;
  env: 'sandbox' | 'production';
  appKey: string;
  discovered: boolean;
  resources: string[];
  mode: string;
  modeValues: Record<string, Record<string, string>>;
  connected: boolean;
}

export interface MergeNodeOption {
  group: string;
  parentId?: string;
  count: number;
  source?: boolean;
  sourceIds?: string[];
}

export interface PickerItem {
  id: string;
  name: string;
  sub: string;
  rank: number;
  group: string | null;
  status: 'show' | 'caveat' | 'hide';
  reason: string | null;
}

export interface PickerModel {
  mode: string;
  rule: string;
  attachTo: import('./node.model').CanvasNode;
  ruleLabel: string;
  items: PickerItem[];
  mergeOpt: MergeNodeOption | null;
  cfg: import('./transform-applicability.model').EpicConfig;
  node: import('./node.model').CanvasNode;
}
