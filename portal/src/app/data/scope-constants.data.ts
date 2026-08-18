// MVP1 resource set — keep in sync with SupportedFhirResourceTypes.All
// (src/FHIRBridge.Domain/Fhir/SupportedFhirResourceTypes.cs) minus Immunization, which the backend
// still supports but MVP1's resource picker deliberately does not surface.
export const FHIR_RESOURCES: string[] = [
  'Patient', 'Practitioner', 'Encounter', 'AllergyIntolerance', 'Observation',
  'Condition', 'Procedure', 'ServiceRequest', 'DiagnosticReport',
  'MedicationRequest', 'MedicationAdministration',
];

/** FHIR resource types Epic sources/destinations support — the canonical list surfaced in the Epic
 *  source wizard's scope generation and the destination wizard's data-group picker. Keep in sync with
 *  SupportedFhirResourceTypes.All (backend) — the original MVP1 11 plus the resources added once their
 *  Epic templates (src/FHIRBridge.Application/Mapping/Catalog/EpicTemplates) existed to catalog them. */
export const SUPPORTED_RESOURCE_TYPES: string[] = [
  'Patient', 'Practitioner', 'Encounter', 'AllergyIntolerance', 'Observation', 'Condition',
  'Procedure', 'ServiceRequest', 'DiagnosticReport', 'MedicationRequest', 'MedicationAdministration',
  'Appointment', 'Binary', 'CarePlan', 'CareTeam', 'Communication', 'CommunicationRequest', 'Device',
  'DocumentReference', 'FamilyMemberHistory', 'Goal', 'ImagingStudy', 'Immunization', 'Location', 'Medication',
  'MedicationDispense', 'MedicationStatement', 'Organization', 'PractitionerRole', 'Provenance',
  'Questionnaire', 'QuestionnaireResponse', 'RelatedPerson', 'Schedule', 'Slot', 'Specimen', 'Task',
];

export const CODED_RESOURCES: string[] = [
  'Observation', 'Condition', 'MedicationRequest', 'MedicationAdministration',
  'AllergyIntolerance', 'Procedure', 'ServiceRequest', 'DiagnosticReport',
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
