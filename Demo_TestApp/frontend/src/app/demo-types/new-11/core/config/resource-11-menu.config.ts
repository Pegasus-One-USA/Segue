// Drives the "New 11" browser's resource selector AND its data table. A flat list of { key, label, apiSegment,
// columns } records — one per curated _11 table. The browser reads every row of the selected resource's table from
// /api/v11/{apiSegment} and renders exactly the columns listed here (in this order), so re-ordering, hiding or
// relabelling a column is a config-only change with no component edit. `id` is the record's own key column.

export interface Resource11Column {
  /** Property name on the API row to read for this column (camelCase, see resource-11.model.ts). */
  key: string;
  label: string;
  /** How to render the raw value. Omitted = plain text. */
  type?: 'date' | 'datetime' | 'number' | 'boolean';
}

export interface Resource11MenuItem {
  /** Stable identifier — also the resource-selector selection key. */
  key: string;
  label: string;
  /** Appended to /api/v11/ for this resource's list endpoint. */
  apiSegment: string;
  columns: Resource11Column[];
}

export const RESOURCE_11_MENU: Resource11MenuItem[] = [
  {
    key: 'patient',
    label: 'Patient',
    apiSegment: 'patients',
    columns: [
      { key: 'patientId', label: 'Patient ID' },
      { key: 'fullName', label: 'Full Name' },
      { key: 'gender', label: 'Gender' },
      { key: 'birthDate', label: 'Date of Birth', type: 'date' },
      { key: 'maritalStatus', label: 'Marital Status' },
      { key: 'race', label: 'Race' },
      { key: 'ethnicity', label: 'Ethnicity' },
      { key: 'phone', label: 'Phone' },
      { key: 'email', label: 'Email' },
      { key: 'addressLine', label: 'Address' },
      { key: 'city', label: 'City' },
      { key: 'state', label: 'State' },
      { key: 'postalCode', label: 'Postal Code' },
      { key: 'country', label: 'Country' },
      { key: 'preferredLanguage', label: 'Language' },
      { key: 'isDeceased', label: 'Deceased', type: 'boolean' },
      { key: 'medicalRecordNumber', label: 'MRN' },
      { key: 'primaryCareProviderName', label: 'Primary Care Provider' },
      { key: 'organizationName', label: 'Organization' },
    ],
  },
  {
    key: 'practitioner',
    label: 'Practitioner',
    apiSegment: 'practitioners',
    columns: [
      { key: 'practitionerId', label: 'Practitioner ID' },
      { key: 'fullName', label: 'Full Name' },
      { key: 'title', label: 'Title' },
      { key: 'credential', label: 'Credential' },
      { key: 'specialty', label: 'Specialty' },
      { key: 'gender', label: 'Gender' },
      { key: 'npi', label: 'NPI' },
      { key: 'phone', label: 'Phone' },
      { key: 'email', label: 'Email' },
      { key: 'addressLine', label: 'Address' },
      { key: 'city', label: 'City' },
      { key: 'state', label: 'State' },
      { key: 'postalCode', label: 'Postal Code' },
      { key: 'isActive', label: 'Active', type: 'boolean' },
    ],
  },
  {
    key: 'encounter',
    label: 'Encounter',
    apiSegment: 'encounters',
    columns: [
      { key: 'encounterId', label: 'Encounter ID' },
      { key: 'patientName', label: 'Patient' },
      { key: 'encounterType', label: 'Type' },
      { key: 'encounterClass', label: 'Class' },
      { key: 'status', label: 'Status' },
      { key: 'startDate', label: 'Start', type: 'date' },
      { key: 'endDate', label: 'End', type: 'date' },
      { key: 'reasonForVisit', label: 'Reason' },
      { key: 'locationName', label: 'Location' },
      { key: 'attendingProviderName', label: 'Attending Provider' },
      { key: 'organizationName', label: 'Organization' },
      { key: 'patientId', label: 'Patient ID' },
    ],
  },
  {
    key: 'observation',
    label: 'Observation',
    apiSegment: 'observations',
    columns: [
      { key: 'observationId', label: 'Observation ID' },
      { key: 'category', label: 'Category' },
      { key: 'observationName', label: 'Name' },
      { key: 'numericValue', label: 'Value', type: 'number' },
      { key: 'unit', label: 'Unit' },
      { key: 'textValue', label: 'Text Value' },
      { key: 'effectiveDateTime', label: 'Effective', type: 'datetime' },
      { key: 'status', label: 'Status' },
      { key: 'interpretation', label: 'Interpretation' },
      { key: 'referenceRange', label: 'Reference Range' },
      { key: 'performedByName', label: 'Performed By' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'condition',
    label: 'Condition',
    apiSegment: 'conditions',
    columns: [
      { key: 'conditionId', label: 'Condition ID' },
      { key: 'conditionName', label: 'Condition' },
      { key: 'diagnosisCode', label: 'Diagnosis Code' },
      { key: 'category', label: 'Category' },
      { key: 'clinicalStatus', label: 'Clinical Status' },
      { key: 'severity', label: 'Severity' },
      { key: 'onsetDate', label: 'Onset', type: 'date' },
      { key: 'recordedDate', label: 'Recorded', type: 'date' },
      { key: 'recordedByName', label: 'Recorded By' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'allergyIntolerance',
    label: 'AllergyIntolerance',
    apiSegment: 'allergy-intolerances',
    columns: [
      { key: 'allergyId', label: 'Allergy ID' },
      { key: 'allergen', label: 'Allergen' },
      { key: 'category', label: 'Category' },
      { key: 'type', label: 'Type' },
      { key: 'criticality', label: 'Criticality' },
      { key: 'clinicalStatus', label: 'Clinical Status' },
      { key: 'reaction', label: 'Reaction' },
      { key: 'onsetDate', label: 'Onset' },
      { key: 'recordedDate', label: 'Recorded', type: 'datetime' },
      { key: 'patientId', label: 'Patient ID' },
    ],
  },
  {
    key: 'medicationRequest',
    label: 'MedicationRequest',
    apiSegment: 'medication-requests',
    columns: [
      { key: 'medicationRequestId', label: 'Request ID' },
      { key: 'medicationName', label: 'Medication' },
      { key: 'status', label: 'Status' },
      { key: 'dosageInstructions', label: 'Dosage Instructions' },
      { key: 'route', label: 'Route' },
      { key: 'doseQuantity', label: 'Dose', type: 'number' },
      { key: 'doseUnit', label: 'Dose Unit' },
      { key: 'frequency', label: 'Frequency' },
      { key: 'dispenseQuantity', label: 'Dispense Qty', type: 'number' },
      { key: 'dispenseUnit', label: 'Dispense Unit' },
      { key: 'refills', label: 'Refills', type: 'number' },
      { key: 'daysSupply', label: 'Days Supply', type: 'number' },
      { key: 'prescribedDate', label: 'Prescribed', type: 'date' },
      { key: 'reason', label: 'Reason' },
      { key: 'prescriberName', label: 'Prescriber' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'medicationAdministration',
    label: 'MedicationAdministration',
    apiSegment: 'medication-administrations',
    columns: [
      { key: 'medicationAdministrationId', label: 'Administration ID' },
      { key: 'medicationName', label: 'Medication' },
      { key: 'status', label: 'Status' },
      { key: 'administeredDateTime', label: 'Administered', type: 'datetime' },
      { key: 'administeredStartTime', label: 'Start', type: 'datetime' },
      { key: 'administeredEndTime', label: 'End', type: 'datetime' },
      { key: 'doseQuantity', label: 'Dose', type: 'number' },
      { key: 'doseUnit', label: 'Dose Unit' },
      { key: 'route', label: 'Route' },
      { key: 'method', label: 'Method' },
      { key: 'bodySite', label: 'Body Site' },
      { key: 'reason', label: 'Reason' },
      { key: 'notes', label: 'Notes' },
      { key: 'administeredByName', label: 'Administered By' },
      { key: 'medicationRequestId', label: 'Request ID' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'serviceRequest',
    label: 'ServiceRequest',
    apiSegment: 'service-requests',
    columns: [
      { key: 'serviceRequestId', label: 'Service Request ID' },
      { key: 'orderName', label: 'Order' },
      { key: 'orderCode', label: 'Order Code' },
      { key: 'category', label: 'Category' },
      { key: 'status', label: 'Status' },
      { key: 'reason', label: 'Reason' },
      { key: 'orderedDate', label: 'Ordered', type: 'datetime' },
      { key: 'scheduledDate', label: 'Scheduled', type: 'datetime' },
      { key: 'orderedByName', label: 'Ordered By' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'diagnosticReport',
    label: 'DiagnosticReport',
    apiSegment: 'diagnostic-reports',
    columns: [
      { key: 'diagnosticReportId', label: 'Report ID' },
      { key: 'reportName', label: 'Report' },
      { key: 'category', label: 'Category' },
      { key: 'status', label: 'Status' },
      { key: 'effectiveDateTime', label: 'Effective', type: 'datetime' },
      { key: 'issuedDateTime', label: 'Issued', type: 'datetime' },
      { key: 'conclusion', label: 'Conclusion' },
      { key: 'reportDocumentUrl', label: 'Document' },
      { key: 'performedByName', label: 'Performed By' },
      { key: 'serviceRequestId', label: 'Service Request ID' },
      { key: 'patientId', label: 'Patient ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
  {
    key: 'procedure',
    label: 'Procedure',
    apiSegment: 'procedures',
    columns: [
      { key: 'procedureId', label: 'Procedure ID' },
      { key: 'procedureName', label: 'Procedure' },
      { key: 'procedureCode', label: 'Procedure Code' },
      { key: 'category', label: 'Category' },
      { key: 'status', label: 'Status' },
      { key: 'performedDate', label: 'Performed', type: 'datetime' },
      { key: 'bodySite', label: 'Body Site' },
      { key: 'reason', label: 'Reason' },
      { key: 'locationName', label: 'Location' },
      { key: 'performedByName', label: 'Performed By' },
      { key: 'serviceRequestId', label: 'Service Request ID' },
      { key: 'diagnosticReportId', label: 'Report ID' },
      { key: 'encounterId', label: 'Encounter ID' },
    ],
  },
];

// ---- Groupings used by the New 11 browser -------------------------------------------------------------------
// New 11 is a Patient List -> per-patient tabs master-detail, plus a separate global Practitioners view.

const byKey = (key: string) => RESOURCE_11_MENU.find((m) => m.key === key)!;

/** The Patient resource (its list endpoint /api/v11/patients backs the top-level Patient List). */
export const PATIENT_RESOURCE: Resource11MenuItem = byKey('patient');

/** The global Practitioner resource (/api/v11/practitioners) — a standalone tab, not per-patient. */
export const PRACTITIONER_RESOURCE: Resource11MenuItem = byKey('practitioner');

/** The nine clinical resources shown as tabs once a patient is selected. Each apiSegment doubles as the
 *  /api/v11/patient/{patientId}/{apiSegment} path segment for that patient's rows. */
export const PATIENT_DETAIL_RESOURCES: Resource11MenuItem[] = RESOURCE_11_MENU.filter(
  (m) => m.key !== 'patient' && m.key !== 'practitioner',
);

/** Compact column set for the clickable Patient List table (the full set is wide). */
export const PATIENT_LIST_COLUMNS: Resource11Column[] = [
  { key: 'patientId', label: 'Patient ID' },
  { key: 'fullName', label: 'Name' },
  { key: 'gender', label: 'Gender' },
  { key: 'birthDate', label: 'DOB', type: 'date' },
  { key: 'medicalRecordNumber', label: 'MRN' },
  { key: 'phone', label: 'Phone' },
  { key: 'city', label: 'City' },
  { key: 'state', label: 'State' },
];
