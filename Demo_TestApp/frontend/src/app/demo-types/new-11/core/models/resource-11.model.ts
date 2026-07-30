// TypeScript shapes for the "_11" curated tables, mirroring Demo_TestApp backend's Resource11Entities.cs /
// Resource11Endpoints.cs field-for-field (camelCase). Every field but each record's own id is nullable — these
// tables are populated by an external FHIR-mapping load that doesn't guarantee every column for every row, so the
// browser must treat any value as possibly missing and fall back to a placeholder.

export interface Patient11 {
  patientId: string;
  fullName: string | null;
  firstName: string | null;
  lastName: string | null;
  gender: string | null;
  birthDate: string | null;
  maritalStatus: string | null;
  race: string | null;
  ethnicity: string | null;
  phone: string | null;
  email: string | null;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
  preferredLanguage: string | null;
  isDeceased: boolean | null;
  medicalRecordNumber: string | null;
  primaryCareProviderId: string | null;
  primaryCareProviderName: string | null;
  organizationId: string | null;
  organizationName: string | null;
}

export interface Practitioner11 {
  practitionerId: string;
  fullName: string | null;
  firstName: string | null;
  lastName: string | null;
  title: string | null;
  credential: string | null;
  specialty: string | null;
  gender: string | null;
  npi: string | null;
  phone: string | null;
  email: string | null;
  addressLine: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  isActive: boolean | null;
}

export interface Encounter11 {
  encounterId: string;
  patientId: string | null;
  patientName: string | null;
  encounterType: string | null;
  encounterClass: string | null;
  status: string | null;
  startDate: string | null;
  endDate: string | null;
  reasonForVisit: string | null;
  locationName: string | null;
  attendingProviderId: string | null;
  attendingProviderName: string | null;
  organizationId: string | null;
  organizationName: string | null;
}

export interface Observation11 {
  observationId: string;
  patientId: string | null;
  encounterId: string | null;
  category: string | null;
  observationName: string | null;
  numericValue: number | null;
  unit: string | null;
  textValue: string | null;
  effectiveDateTime: string | null;
  status: string | null;
  interpretation: string | null;
  referenceRange: string | null;
  performedById: string | null;
  performedByName: string | null;
}

export interface Condition11 {
  conditionId: string;
  patientId: string | null;
  encounterId: string | null;
  conditionName: string | null;
  diagnosisCode: string | null;
  category: string | null;
  clinicalStatus: string | null;
  severity: string | null;
  onsetDate: string | null;
  recordedDate: string | null;
  recordedById: string | null;
  recordedByName: string | null;
}

export interface AllergyIntolerance11 {
  allergyId: string;
  patientId: string | null;
  allergen: string | null;
  category: string | null;
  type: string | null;
  criticality: string | null;
  clinicalStatus: string | null;
  reaction: string | null;
  onsetDate: string | null;
  recordedDate: string | null;
}

export interface MedicationRequest11 {
  medicationRequestId: string;
  patientId: string | null;
  encounterId: string | null;
  medicationId: string | null;
  medicationName: string | null;
  status: string | null;
  dosageInstructions: string | null;
  route: string | null;
  doseQuantity: number | null;
  doseUnit: string | null;
  frequency: string | null;
  dispenseQuantity: number | null;
  dispenseUnit: string | null;
  refills: number | null;
  daysSupply: number | null;
  prescribedDate: string | null;
  reason: string | null;
  prescriberId: string | null;
  prescriberName: string | null;
}

export interface MedicationAdministration11 {
  medicationAdministrationId: string;
  patientId: string | null;
  encounterId: string | null;
  medicationRequestId: string | null;
  medicationId: string | null;
  medicationName: string | null;
  status: string | null;
  administeredDateTime: string | null;
  administeredStartTime: string | null;
  administeredEndTime: string | null;
  doseQuantity: number | null;
  doseUnit: string | null;
  route: string | null;
  method: string | null;
  bodySite: string | null;
  reason: string | null;
  notes: string | null;
  administeredById: string | null;
  administeredByName: string | null;
}

export interface ServiceRequest11 {
  serviceRequestId: string;
  patientId: string | null;
  encounterId: string | null;
  orderName: string | null;
  orderCode: string | null;
  category: string | null;
  status: string | null;
  reason: string | null;
  orderedDate: string | null;
  scheduledDate: string | null;
  orderedById: string | null;
  orderedByName: string | null;
}

export interface DiagnosticReport11 {
  diagnosticReportId: string;
  patientId: string | null;
  encounterId: string | null;
  serviceRequestId: string | null;
  reportName: string | null;
  category: string | null;
  status: string | null;
  effectiveDateTime: string | null;
  issuedDateTime: string | null;
  conclusion: string | null;
  reportDocumentUrl: string | null;
  performedById: string | null;
  performedByName: string | null;
}

export interface Procedure11 {
  procedureId: string;
  patientId: string | null;
  encounterId: string | null;
  serviceRequestId: string | null;
  diagnosticReportId: string | null;
  procedureName: string | null;
  procedureCode: string | null;
  category: string | null;
  status: string | null;
  performedDate: string | null;
  bodySite: string | null;
  reason: string | null;
  locationName: string | null;
  performedById: string | null;
  performedByName: string | null;
}
