import { Transform } from '../models/transform.model';

// Destination-TYPE rows (SQL Server, Aidbox, Medplum, Azure Blob Storage, Snowflake, ...) used to
// live here (rank 7) — they now come from the canonical Node Catalog (GET /api/v1/permissions/
// node-catalog; see node-catalog.model.ts / node-catalog-legacy-ids.ts), the same source Role
// Permissions' Workflow Nodes section reads from. This file now only holds pipeline STEPS, which
// have no SourceSystemType/DestinationType backing and stay outside that consolidation.
export const TRANSFORMS: Transform[] = [
  { id: 'fhir-validation',  rank: 2, name: 'FHIR Validation',                     sub: 'Validate resources against US Core / base R4 profiles.' },
  { id: 'normalize',        rank: 3, group: 'normalize', name: 'Normalize Data',          sub: 'Flatten extensions, score quality, tag US Core.' },
  { id: 'patient-matching', rank: 3, group: 'normalize', name: 'Patient Matching (MPI)',  sub: 'Match patients against a master patient index.' },
  { id: 'merge-patients',   rank: 3, group: 'normalize', name: 'Merge Patients',          sub: 'Merge duplicate patient records.' },
  { id: 'terminology',      rank: 4, name: 'Terminology Mapping',                  sub: 'Validate / translate ICD, SNOMED, LOINC, RxNorm codes.' },
  { id: 'deid-safeharbor',  rank: 5, group: 'deid', name: 'De-identification · Safe Harbor', sub: 'Per-resource HIPAA Safe Harbor redaction.' },
  { id: 'deid-kanon',       rank: 5, group: 'deid', name: 'De-identification · k-anonymity',  sub: 'Cohort generalization + suppression.' },
  { id: 'field-mapping',    rank: 6, name: 'Field Mapping',                        sub: 'Map FHIR paths to destination fields.' },
  { id: 'audit-lineage',    rank: 8, category: 'Audit & Lineage', name: 'Audit & Lineage', sub: 'Hash-chained audit + record-level lineage.' },
  { id: 'hedis',            rank: 9, category: 'Analytics',    name: 'HEDIS Measure Report', sub: 'Compute HEDIS quality measures.' },
  { id: 'anomaly',          rank: 9, category: 'Analytics',    name: 'Anomaly Detection',  sub: 'Flag statistical anomalies.' },
  { id: 'patient-agg',      rank: 9, category: 'Analytics',    name: 'Patient Aggregation', sub: 'Aggregate a patient-360 view.' },
];

export const REPEATABLE_TRANSFORMS = new Set(['field-mapping']);
