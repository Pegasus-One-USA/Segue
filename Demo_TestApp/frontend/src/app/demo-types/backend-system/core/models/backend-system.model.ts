// Mirrors Demo_TestApp backend's BackendSystemEndpoints.cs DTOs field-for-field. Every field but each record's
// own id is nullable, matching what the API actually returns — these tables are populated by upstream FHIR-mapping
// pipelines that don't guarantee every column is filled in for every row (requirement section 12, Null Handling).
// Angular templates must use safe navigation/placeholders rather than assume any field is present.

export interface PatientListItem {
  patientId: string;
  fullName: string | null;
  mrn: string | null;
  identifier: string | null;
  gender: string | null;
  birthDate: string | null;
}

export interface PatientDetail {
  patientId: string;
  identifier: string | null;
  mrn: string | null;
  familyName: string | null;
  givenName: string | null;
  middleName: string | null;
  fullName: string | null;
  gender: string | null;
  birthDate: string | null;
  deceased: boolean | null;
  maritalStatus: string | null;
  phone: string | null;
  email: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
}

export interface Practitioner {
  practitionerId: string;
  identifier: string | null;
  npi: string | null;
  fullName: string | null;
  gender: string | null;
  qualification: string | null;
  phone: string | null;
  email: string | null;
}

export interface Encounter {
  encounterId: string;
  patientId: string | null;
  practitionerId: string | null;
  identifier: string | null;
  status: string | null;
  class: string | null;
  type: string | null;
  priority: string | null;
  startDateTime: string | null;
  endDateTime: string | null;
  serviceProvider: string | null;
  reasonCode: string | null;
}

export interface AllergyIntolerance {
  allergyIntoleranceId: string;
  patientId: string | null;
  encounterId: string | null;
  identifier: string | null;
  clinicalStatus: string | null;
  verificationStatus: string | null;
  category: string | null;
  criticality: string | null;
  code: string | null;
  substance: string | null;
  reaction: string | null;
  severity: string | null;
  onsetDateTime: string | null;
  recordedDate: string | null;
}

export interface Observation {
  observationId: string;
  patientId: string | null;
  encounterId: string | null;
  identifier: string | null;
  status: string | null;
  category: string | null;
  code: string | null;
  value: string | null;
  unit: string | null;
  interpretation: string | null;
  effectiveDateTime: string | null;
  issuedDateTime: string | null;
}

export interface Condition {
  conditionId: string;
  patientId: string | null;
  encounterId: string | null;
  identifier: string | null;
  clinicalStatus: string | null;
  verificationStatus: string | null;
  category: string | null;
  severity: string | null;
  code: string | null;
  bodySite: string | null;
  onsetDateTime: string | null;
  abatementDateTime: string | null;
  recordedDate: string | null;
}

export interface Procedure {
  procedureId: string;
  patientId: string | null;
  encounterId: string | null;
  practitionerId: string | null;
  identifier: string | null;
  status: string | null;
  category: string | null;
  code: string | null;
  bodySite: string | null;
  outcome: string | null;
  performedStartDateTime: string | null;
  performedEndDateTime: string | null;
}

export interface ServiceRequest {
  serviceRequestId: string;
  patientId: string | null;
  encounterId: string | null;
  practitionerId: string | null;
  identifier: string | null;
  status: string | null;
  intent: string | null;
  priority: string | null;
  category: string | null;
  code: string | null;
  authoredOn: string | null;
  occurrenceDateTime: string | null;
  reasonCode: string | null;
}

export interface DiagnosticReport {
  diagnosticReportId: string;
  patientId: string | null;
  encounterId: string | null;
  identifier: string | null;
  status: string | null;
  category: string | null;
  code: string | null;
  effectiveDateTime: string | null;
  issuedDateTime: string | null;
  conclusion: string | null;
}

export interface MedicationRequest {
  medicationRequestId: string;
  patientId: string | null;
  encounterId: string | null;
  practitionerId: string | null;
  identifier: string | null;
  status: string | null;
  intent: string | null;
  priority: string | null;
  medicationCode: string | null;
  dosageInstruction: string | null;
  authoredOn: string | null;
}

export interface MedicationAdministration {
  medicationAdministrationId: string;
  patientId: string | null;
  encounterId: string | null;
  medicationRequestId: string | null;
  identifier: string | null;
  status: string | null;
  medicationCode: string | null;
  dosage: string | null;
  route: string | null;
  effectiveDateTime: string | null;
  note: string | null;
}
