// Destination-TYPE rows (dest-sqlserver, dest-fhir, ...) no longer live in TRANSFORMS/this model —
// they, and their permission codes, come from the canonical Node Catalog (node-catalog.model.ts)
// instead. This interface now only describes pipeline steps (Field Mapping, Validation, Normalize,
// Terminology, De-identification, Audit & Lineage, HEDIS, Anomaly, Patient Aggregation, ...), which
// have no SourceSystemType/DestinationType backing and stay outside the Node Catalog consolidation.
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
