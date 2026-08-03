// Static today, but shaped exactly like a row from a future "BackendSystem menu/resource metadata" table would
// be: a flat list of { key, label, apiSegment, columns } records. PatientDetailsComponent's left nav and resource
// table are both driven entirely off this array (see backend-system-menu.config.ts consumers) — swapping this
// constant for an HTTP-loaded signal later requires no template/component redesign, only where the array's value
// comes from.

export interface ResourceColumn {
  /** Property name on the resource's model (see backend-system.model.ts) to read for this column. */
  key: string;
  label: string;
  /** 'date' renders the raw ISO value through Angular's date pipe; 'boolean' renders Yes/No/'-'. */
  type?: 'date' | 'boolean';
}

export interface ResourceMenuItem {
  /** Stable identifier — also used as the left-nav selection key. */
  key: string;
  label: string;
  /** Appended to /api/backend-system/patient/{patientId}/ for the list endpoints; empty for the Patient menu,
   *  which instead uses GET /api/backend-system/patient/{patientId} (see BackendSystemService.getPatient). */
  apiSegment: string;
  columns: ResourceColumn[];
}

export const BACKEND_SYSTEM_MENU: ResourceMenuItem[] = [
  {
    key: 'patient',
    label: 'Patient',
    apiSegment: '',
    columns: [
      { key: 'fullName', label: 'Name' },
      { key: 'mrn', label: 'MRN' },
      { key: 'identifier', label: 'Identifier' },
      { key: 'gender', label: 'Gender' },
      { key: 'birthDate', label: 'Date of Birth', type: 'date' },
      { key: 'deceased', label: 'Deceased', type: 'boolean' },
      { key: 'maritalStatus', label: 'Marital Status' },
      { key: 'phone', label: 'Phone' },
      { key: 'email', label: 'Email' },
      { key: 'addressLine1', label: 'Address Line 1' },
      { key: 'addressLine2', label: 'Address Line 2' },
      { key: 'city', label: 'City' },
      { key: 'state', label: 'State' },
      { key: 'postalCode', label: 'Postal Code' },
      { key: 'country', label: 'Country' },
    ],
  },
  {
    key: 'encounter',
    label: 'Encounter',
    apiSegment: 'encounters',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'class', label: 'Class' },
      { key: 'type', label: 'Type' },
      { key: 'priority', label: 'Priority' },
      { key: 'startDateTime', label: 'Start', type: 'date' },
      { key: 'endDateTime', label: 'End', type: 'date' },
      { key: 'serviceProvider', label: 'Service Provider' },
      { key: 'reasonCode', label: 'Reason' },
    ],
  },
  {
    key: 'allergyIntolerance',
    label: 'AllergyIntolerance',
    apiSegment: 'allergy-intolerances',
    columns: [
      { key: 'clinicalStatus', label: 'Clinical Status' },
      { key: 'verificationStatus', label: 'Verification Status' },
      { key: 'category', label: 'Category' },
      { key: 'criticality', label: 'Criticality' },
      { key: 'code', label: 'Code' },
      { key: 'substance', label: 'Substance' },
      { key: 'reaction', label: 'Reaction' },
      { key: 'severity', label: 'Severity' },
      { key: 'onsetDateTime', label: 'Onset', type: 'date' },
      { key: 'recordedDate', label: 'Recorded', type: 'date' },
    ],
  },
  {
    key: 'observation',
    label: 'Observation',
    apiSegment: 'observations',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'category', label: 'Category' },
      { key: 'code', label: 'Code' },
      { key: 'value', label: 'Value' },
      { key: 'unit', label: 'Unit' },
      { key: 'interpretation', label: 'Interpretation' },
      { key: 'effectiveDateTime', label: 'Effective', type: 'date' },
      { key: 'issuedDateTime', label: 'Issued', type: 'date' },
    ],
  },
  {
    key: 'condition',
    label: 'Condition',
    apiSegment: 'conditions',
    columns: [
      { key: 'clinicalStatus', label: 'Clinical Status' },
      { key: 'verificationStatus', label: 'Verification Status' },
      { key: 'category', label: 'Category' },
      { key: 'severity', label: 'Severity' },
      { key: 'code', label: 'Code' },
      { key: 'bodySite', label: 'Body Site' },
      { key: 'onsetDateTime', label: 'Onset', type: 'date' },
      { key: 'abatementDateTime', label: 'Abatement', type: 'date' },
      { key: 'recordedDate', label: 'Recorded', type: 'date' },
    ],
  },
  {
    key: 'procedure',
    label: 'Procedure',
    apiSegment: 'procedures',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'category', label: 'Category' },
      { key: 'code', label: 'Code' },
      { key: 'bodySite', label: 'Body Site' },
      { key: 'outcome', label: 'Outcome' },
      { key: 'performedStartDateTime', label: 'Performed Start', type: 'date' },
      { key: 'performedEndDateTime', label: 'Performed End', type: 'date' },
    ],
  },
  {
    key: 'serviceRequest',
    label: 'ServiceRequest',
    apiSegment: 'service-requests',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'intent', label: 'Intent' },
      { key: 'priority', label: 'Priority' },
      { key: 'category', label: 'Category' },
      { key: 'code', label: 'Code' },
      { key: 'authoredOn', label: 'Authored On', type: 'date' },
      { key: 'occurrenceDateTime', label: 'Occurrence', type: 'date' },
      { key: 'reasonCode', label: 'Reason' },
    ],
  },
  {
    key: 'diagnosticReport',
    label: 'DiagnosticReport',
    apiSegment: 'diagnostic-reports',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'category', label: 'Category' },
      { key: 'code', label: 'Code' },
      { key: 'effectiveDateTime', label: 'Effective', type: 'date' },
      { key: 'issuedDateTime', label: 'Issued', type: 'date' },
      { key: 'conclusion', label: 'Conclusion' },
    ],
  },
  {
    key: 'medicationRequest',
    label: 'MedicationRequest',
    apiSegment: 'medication-requests',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'intent', label: 'Intent' },
      { key: 'priority', label: 'Priority' },
      { key: 'medicationCode', label: 'Medication Code' },
      { key: 'dosageInstruction', label: 'Dosage Instruction' },
      { key: 'authoredOn', label: 'Authored On', type: 'date' },
    ],
  },
  {
    key: 'medicationAdministration',
    label: 'MedicationAdministration',
    apiSegment: 'medication-administrations',
    columns: [
      { key: 'status', label: 'Status' },
      { key: 'medicationCode', label: 'Medication Code' },
      { key: 'dosage', label: 'Dosage' },
      { key: 'route', label: 'Route' },
      { key: 'effectiveDateTime', label: 'Effective', type: 'date' },
      { key: 'note', label: 'Note' },
    ],
  },
];
