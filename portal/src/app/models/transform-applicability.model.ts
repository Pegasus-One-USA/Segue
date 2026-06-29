export type ApplicabilityStatus = 'show' | 'caveat' | 'hide';

export interface ApplicabilityResult {
  id: string;
  name: string;
  sub: string;
  rank: number;
  group: string | null;
  status: ApplicabilityStatus;
  reason: string | null;
  code: string | null;
}

export interface ApplicabilityOrigin {
  rank: number;
  existingTransformIds: string[];
}

export interface EpicConfig {
  context: string;
  ingestionMode: string;
  resources: string[];
}
