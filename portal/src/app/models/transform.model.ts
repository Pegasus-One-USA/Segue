export interface Transform {
  id: string;
  rank: number;
  group?: string;
  category?: string;
  name: string;
  sub: string;
}

export const RANK_LABEL: Record<number, string> = {
  0: 'Source',
  1: 'Consent',
  2: 'Validation',
  3: 'Normalize',
  4: 'Terminology',
  5: 'De-identify',
  6: 'Map / reshape',
  7: 'Destination',
  8: 'Audit & lineage',
  9: 'Analytics',
};
