export const FHIR_RESOURCES: string[] = [
  'Patient', 'Observation', 'Condition', 'MedicationRequest', 'AllergyIntolerance',
  'Encounter', 'Procedure', 'Immunization', 'DiagnosticReport', 'DocumentReference',
];

export const CODED_RESOURCES: string[] = [
  'Observation', 'Condition', 'MedicationRequest', 'AllergyIntolerance',
  'Procedure', 'Immunization', 'DiagnosticReport',
];

export const CTX_TRANSFORMS: Record<string, string[]> = {
  'Patient (standalone)':  ['Normalization', 'Terminology', 'US Core validation', 'Consent enforcement'],
  'Provider (EHR launch)': ['Normalization', 'Mapping', 'Terminology', 'US Core validation'],
  'Provider (standalone)': ['Normalization', 'Mapping', 'Terminology', 'US Core validation'],
  'Backend system':        ['Normalization', 'Mapping', 'Terminology', 'US Core validation', 'De-identification', 'Audit & lineage'],
};

export const REASON: Record<string, string> = {
  SINGLE_PATIENT_NO_COHORT: 'This Epic app is scoped to one patient, so there is no population to act on.',
  STREAM_SINGLE_RESOURCE:   'Each notification carries a single resource — there is nothing to combine or aggregate.',
  NEEDS_FULL_DATASET:       'Needs a complete dataset; on an incremental pull the result may be statistically weak.',
  ACCUMULATE_FIRST:         'Usually belongs after data is accumulated in a destination, not inline on each batch.',
  NO_CODED_RESOURCES:       'No coded resources are in scope, so there is little for this step to map.',
  DEID_UNUSUAL_PATIENT:     'De-identifying a patient\'s own data in a patient-facing flow is unusual.',
  NO_PATIENT_RESOURCE:      'Patient resource is not in scope, so patient-level matching cannot run.',
  PROVIDER_PARTIAL:         'Provider context may see a partial population; results depend on the working set.',
  BACKWARD_RANK:            'Pipeline must move strictly forward — this step ranks at or before the current step.',
  ALREADY_IN_CHAIN:         'This transform already exists in the chain; it runs once per pipeline.',
};
