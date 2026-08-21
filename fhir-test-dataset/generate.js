#!/usr/bin/env node
/*
 * FHIR R4 (4.0.1) interconnected test-dataset generator.
 *
 * Builds the entire resource graph in memory with deterministic alphanumeric
 * ids, wires every cross-reference programmatically (so referential integrity
 * is guaranteed), then emits:
 *   - resources/<ResourceType>/<id>.json     one file per resource
 *   - bundles/hapi-aidbox/*.json             PUT transaction bundles (client ids, RI-safe)
 *   - bundles/medplum/*.json                 POST transaction bundles (urn:uuid + ifNoneExist)
 *   - negative/*.json                        intentionally broken-reference resources
 *   - manifest.json                          machine-readable index
 *   - validation-report.md                   reference-validation results (reflects reality)
 *
 * Target server: HAPI FHIR 8.10.0, R4 4.0.1, https://segue.pegasusone.com:7011/fhir
 * GENERATOR ONLY - never uploads anything.
 */
'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = __dirname;
const BASE_URL = 'https://segue.pegasusone.com:7011/fhir';

// ---------------------------------------------------------------------------
// Terminology systems (genuine standard code systems - codes below are real,
// commonly used codes; terminology membership is NOT server-validated here).
// ---------------------------------------------------------------------------
const SYS = {
  SNOMED: 'http://snomed.info/sct',
  LOINC: 'http://loinc.org',
  RXNORM: 'http://www.nlm.nih.gov/research/umls/rxnorm',
  ICD10: 'http://hl7.org/fhir/sid/icd-10-cm',
  CVX: 'http://hl7.org/fhir/sid/cvx',
  CPT: 'http://www.ama-assn.org/go/cpt',
  UCUM: 'http://unitsofmeasure.org',
};

// ---------------------------------------------------------------------------
// Resource registry
// ---------------------------------------------------------------------------
const registry = []; // { resourceType, id, category, resource }
const byKey = new Map(); // "Type/id" -> entry

function biz(type, id) {
  return [{ system: `urn:segue:test:${type.toLowerCase()}`, value: id.toUpperCase() }];
}

function meta() {
  return {
    source: 'urn:segue:test-dataset',
    tag: [{ system: 'urn:segue:test:dataset', code: 'synthetic', display: 'Synthetic test data' }],
  };
}

function add(resourceType, id, body, category) {
  const resource = Object.assign(
    { resourceType, id, meta: meta(), identifier: biz(resourceType, id) },
    body
  );
  const entry = { resourceType, id, category: category || 'valid', resource };
  registry.push(entry);
  byKey.set(`${resourceType}/${id}`, entry);
  return entry;
}

// Relative reference helper
function ref(type, id, display) {
  const r = { reference: `${type}/${id}` };
  if (display) r.display = display;
  return r;
}
// Absolute reference helper (reference-style test case)
function absRef(type, id, display) {
  const r = { reference: `${BASE_URL}/${type}/${id}` };
  if (display) r.display = display;
  return r;
}
// Identifier-based (logical) reference helper (reference-style test case)
function idRef(system, value, type, display) {
  const r = { identifier: { system, value } };
  if (type) r.type = type;
  if (display) r.display = display;
  return r;
}

function cc(system, code, display) {
  return { coding: [{ system, code, display }], text: display };
}

// ===========================================================================
// FOUNDATION resources
// ===========================================================================

// Organizations
add('Organization', 'org-001', {
  active: true,
  type: [cc('http://terminology.hl7.org/CodeSystem/organization-type', 'prov', 'Healthcare Provider')],
  name: 'Segue General Hospital',
  telecom: [{ system: 'phone', value: '+1-555-0100', use: 'work' }],
  address: [{ line: ['100 Health Way'], city: 'Boston', state: 'MA', postalCode: '02115', country: 'US' }],
});
add('Organization', 'org-002', {
  active: true,
  type: [cc('http://terminology.hl7.org/CodeSystem/organization-type', 'pay', 'Payer')],
  name: 'BlueShield Health Plan',
  telecom: [{ system: 'phone', value: '+1-555-0200', use: 'work' }],
});

// Locations (belong to org-001)
add('Location', 'location-001', {
  status: 'active',
  name: 'Segue General - Outpatient Clinic',
  mode: 'instance',
  type: [cc('http://terminology.hl7.org/CodeSystem/v3-RoleCode', 'OF', 'Outpatient Facility')],
  physicalType: cc('http://terminology.hl7.org/CodeSystem/location-physical-type', 'bu', 'Building'),
  managingOrganization: ref('Organization', 'org-001', 'Segue General Hospital'),
});
add('Location', 'location-002', {
  status: 'active',
  name: 'Segue General - Inpatient Ward 3',
  mode: 'instance',
  type: [cc('http://terminology.hl7.org/CodeSystem/v3-RoleCode', 'HOSP', 'Hospital')],
  physicalType: cc('http://terminology.hl7.org/CodeSystem/location-physical-type', 'wa', 'Ward'),
  managingOrganization: ref('Organization', 'org-001', 'Segue General Hospital'),
});

// Practitioners
add('Practitioner', 'practitioner-001', {
  active: true,
  identifier: [
    { system: 'urn:segue:test:practitioner', value: 'PRACTITIONER-001' },
    { system: 'http://hl7.org/fhir/sid/us-npi', value: '1234567893' },
  ],
  name: [{ family: 'Reynolds', given: ['Sarah'], prefix: ['Dr'] }],
  gender: 'female',
  telecom: [{ system: 'phone', value: '+1-555-0111', use: 'work' }],
});
add('Practitioner', 'practitioner-002', {
  active: true,
  identifier: [
    { system: 'urn:segue:test:practitioner', value: 'PRACTITIONER-002' },
    { system: 'http://hl7.org/fhir/sid/us-npi', value: '1245319599' },
  ],
  name: [{ family: 'Okafor', given: ['Daniel'], prefix: ['Dr'] }],
  gender: 'male',
});
add('Practitioner', 'practitioner-003', {
  active: true,
  identifier: [
    { system: 'urn:segue:test:practitioner', value: 'PRACTITIONER-003' },
    { system: 'http://hl7.org/fhir/sid/us-npi', value: '1679576722' },
  ],
  name: [{ family: 'Tanaka', given: ['Yuki'], prefix: ['Dr'] }],
  gender: 'female',
});

// PractitionerRole (Organization <- PractitionerRole -> Practitioner)
add('PractitionerRole', 'practitionerrole-001', {
  active: true,
  practitioner: ref('Practitioner', 'practitioner-001', 'Dr Sarah Reynolds'),
  organization: ref('Organization', 'org-001', 'Segue General Hospital'),
  code: [cc('http://terminology.hl7.org/CodeSystem/practitioner-role', 'doctor', 'Doctor')],
  specialty: [cc(SYS.SNOMED, '419192003', 'Internal medicine')],
  location: [ref('Location', 'location-001', 'Outpatient Clinic')],
});
add('PractitionerRole', 'practitionerrole-002', {
  active: true,
  practitioner: ref('Practitioner', 'practitioner-003', 'Dr Yuki Tanaka'),
  organization: ref('Organization', 'org-001', 'Segue General Hospital'),
  code: [cc('http://terminology.hl7.org/CodeSystem/practitioner-role', 'doctor', 'Doctor')],
  specialty: [cc(SYS.SNOMED, '418112009', 'Pulmonary medicine')],
  location: [ref('Location', 'location-001', 'Outpatient Clinic')],
});

// Medications
add('Medication', 'medication-001', {
  status: 'active',
  code: cc(SYS.RXNORM, '860975', 'Metformin hydrochloride 500 MG Oral Tablet'),
  form: cc(SYS.SNOMED, '385055001', 'Tablet'),
});
add('Medication', 'medication-002', {
  status: 'active',
  code: cc(SYS.RXNORM, '314076', 'Lisinopril 10 MG Oral Tablet'),
  form: cc(SYS.SNOMED, '385055001', 'Tablet'),
});
add('Medication', 'medication-003', {
  status: 'active',
  code: cc(SYS.RXNORM, '308191', 'Amoxicillin 500 MG Oral Capsule'),
  form: cc(SYS.SNOMED, '385049006', 'Capsule'),
});

// Devices (Device.patient -> Patient wired after patients exist; store subject ids)
add('Device', 'device-001', {
  status: 'active',
  manufacturer: 'Acme MedTech',
  deviceName: [{ name: 'Acme GlucoTrack Meter', type: 'user-friendly-name' }],
  type: cc(SYS.SNOMED, '337414009', 'Blood glucose meter'),
  patient: ref('Patient', 'patient-001', 'James Whitfield'),
});
add('Device', 'device-002', {
  status: 'active',
  manufacturer: 'Acme MedTech',
  deviceName: [{ name: 'Acme OxySense Pulse Oximeter', type: 'user-friendly-name' }],
  type: cc(SYS.SNOMED, '448703006', 'Pulse oximeter'),
  patient: ref('Patient', 'patient-003', 'Maria Delgado'),
});

// ===========================================================================
// PATIENTS (5 distinct clinical scenarios)
// ===========================================================================
function patient(id, family, given, gender, birthDate, extra) {
  return add('Patient', id, Object.assign({
    active: true,
    name: [{ use: 'official', family, given }],
    gender,
    birthDate,
    telecom: [{ system: 'phone', value: '+1-555-1' + id.slice(-3), use: 'home' }],
    address: [{ line: ['1 Elm St'], city: 'Boston', state: 'MA', postalCode: '02120', country: 'US' }],
    managingOrganization: ref('Organization', 'org-001', 'Segue General Hospital'),
  }, extra || {}));
}
patient('patient-001', 'Whitfield', ['James', 'A'], 'male', '1968-03-12');   // Type 2 Diabetes
patient('patient-002', 'Nguyen', ['Linda'], 'female', '1972-11-30');         // Hypertension
patient('patient-003', 'Delgado', ['Maria'], 'female', '1959-07-08');        // Respiratory (COPD)
patient('patient-004', 'Brooks', ['Robert'], 'male', '1945-01-22');          // Acute hospitalization
patient('patient-005', 'Chen', ['Grace'], 'female', '1990-05-17');           // Preventive care

// RelatedPerson (-> Patient)
add('RelatedPerson', 'relatedperson-001', {
  active: true,
  patient: ref('Patient', 'patient-001', 'James Whitfield'),
  relationship: [cc('http://terminology.hl7.org/CodeSystem/v3-RoleCode', 'SPS', 'Spouse')],
  name: [{ family: 'Whitfield', given: ['Karen'] }],
  gender: 'female',
  telecom: [{ system: 'phone', value: '+1-555-1991', use: 'home' }],
});
add('RelatedPerson', 'relatedperson-002', {
  active: true,
  patient: ref('Patient', 'patient-004', 'Robert Brooks'),
  relationship: [cc('http://terminology.hl7.org/CodeSystem/v3-RoleCode', 'DAUC', 'Daughter')],
  name: [{ family: 'Brooks', given: ['Emily'] }],
  gender: 'female',
});

// Coverage (beneficiary -> Patient, payor -> Organization)
add('Coverage', 'coverage-001', {
  status: 'active',
  beneficiary: ref('Patient', 'patient-001', 'James Whitfield'),
  payor: [ref('Organization', 'org-002', 'BlueShield Health Plan')],
  subscriberId: 'BS-0001',
  relationship: cc('http://terminology.hl7.org/CodeSystem/subscriber-relationship', 'self', 'Self'),
});
add('Coverage', 'coverage-002', {
  status: 'active',
  beneficiary: ref('Patient', 'patient-002', 'Linda Nguyen'),
  payor: [ref('Organization', 'org-002', 'BlueShield Health Plan')],
  subscriberId: 'BS-0002',
  relationship: cc('http://terminology.hl7.org/CodeSystem/subscriber-relationship', 'self', 'Self'),
});
// coverage-003 uses an IDENTIFIER-BASED (logical) reference for payor (reference-style case)
add('Coverage', 'coverage-003', {
  status: 'active',
  beneficiary: ref('Patient', 'patient-004', 'Robert Brooks'),
  payor: [idRef('urn:segue:test:organization', 'ORG-002', undefined, 'BlueShield Health Plan (by identifier)')],
  subscriberId: 'BS-0004',
  relationship: cc('http://terminology.hl7.org/CodeSystem/subscriber-relationship', 'self', 'Self'),
}, 'reference-style');

// ===========================================================================
// ENCOUNTERS (nested backbone references: participant.individual, location.location)
// ===========================================================================
function encounter(id, patId, patDisplay, klass, klassDisplay, practId, practDisplay, locId, proIsAbsolute) {
  const subject = proIsAbsolute
    ? absRef('Patient', patId, patDisplay) // absolute reference (reference-style case)
    : ref('Patient', patId, patDisplay);
  return add('Encounter', id, {
    status: 'finished',
    class: { system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode', code: klass, display: klassDisplay },
    subject,
    participant: [{
      type: [cc('http://terminology.hl7.org/CodeSystem/v3-ParticipationType', 'ATND', 'attender')],
      individual: ref('Practitioner', practId, practDisplay), // reference nested in backbone element
    }],
    period: { start: '2026-05-04T09:00:00Z', end: '2026-05-04T09:45:00Z' },
    serviceProvider: ref('Organization', 'org-001', 'Segue General Hospital'),
    location: [{ location: ref('Location', locId, 'Clinic/Ward') }], // reference nested in backbone element
  }, proIsAbsolute ? 'reference-style' : 'valid');
}
// encounter-001 subject uses an ABSOLUTE reference (reference-style case)
encounter('encounter-001', 'patient-001', 'James Whitfield', 'AMB', 'ambulatory', 'practitioner-001', 'Dr Sarah Reynolds', 'location-001', true);
encounter('encounter-002', 'patient-003', 'Maria Delgado', 'AMB', 'ambulatory', 'practitioner-003', 'Dr Yuki Tanaka', 'location-001', false);
encounter('encounter-003', 'patient-004', 'Robert Brooks', 'IMP', 'inpatient encounter', 'practitioner-002', 'Dr Daniel Okafor', 'location-002', false);

// ===========================================================================
// CONDITIONS (5) - Condition.subject, Condition.encounter
// ===========================================================================
function condition(id, patId, patDisplay, encId, snomed, snomedDisplay, icd10, icd10Display, onset) {
  const code = { coding: [
    { system: SYS.SNOMED, code: snomed, display: snomedDisplay },
    { system: SYS.ICD10, code: icd10, display: icd10Display },
  ], text: snomedDisplay };
  const body = {
    clinicalStatus: cc('http://terminology.hl7.org/CodeSystem/condition-clinical', 'active', 'Active'),
    verificationStatus: cc('http://terminology.hl7.org/CodeSystem/condition-ver-status', 'confirmed', 'Confirmed'),
    category: [cc('http://terminology.hl7.org/CodeSystem/condition-category', 'problem-list-item', 'Problem List Item')],
    code,
    subject: ref('Patient', patId, patDisplay),
    onsetDateTime: onset,
  };
  if (encId) body.encounter = ref('Encounter', encId, encId);
  return add('Condition', id, body);
}
condition('condition-001', 'patient-001', 'James Whitfield', 'encounter-001', '44054006', 'Type 2 diabetes mellitus', 'E11.9', 'Type 2 diabetes mellitus without complications', '2019-02-01');
condition('condition-002', 'patient-002', 'Linda Nguyen', null, '59621000', 'Essential hypertension', 'I10', 'Essential (primary) hypertension', '2020-06-15');
condition('condition-003', 'patient-003', 'Maria Delgado', 'encounter-002', '13645005', 'Chronic obstructive pulmonary disease', 'J44.9', 'COPD, unspecified', '2018-10-10');
condition('condition-004', 'patient-004', 'Robert Brooks', 'encounter-003', '233604007', 'Pneumonia', 'J18.9', 'Pneumonia, unspecified organism', '2026-05-01');
condition('condition-005', 'patient-001', 'James Whitfield', 'encounter-001', '59621000', 'Essential hypertension', 'I10', 'Essential (primary) hypertension', '2021-03-20');

// ===========================================================================
// OBSERVATIONS (10) - Observation.subject, Observation.encounter
// ===========================================================================
function vitalsCat() {
  return [cc('http://terminology.hl7.org/CodeSystem/observation-category', 'vital-signs', 'Vital Signs')];
}
function labCat() {
  return [cc('http://terminology.hl7.org/CodeSystem/observation-category', 'laboratory', 'Laboratory')];
}
function obs(id, patId, patDisplay, encId, category, code, valueQty, effective, extra) {
  const body = Object.assign({
    status: 'final',
    category,
    code,
    subject: ref('Patient', patId, patDisplay),
    effectiveDateTime: effective,
  }, extra || {});
  if (encId) body.encounter = ref('Encounter', encId, encId);
  if (valueQty) body.valueQuantity = valueQty;
  return add('Observation', id, body);
}
function q(value, unit, code) { return { value, unit, system: SYS.UCUM, code }; }

// patient-001 diabetes: glucose, HbA1c, blood pressure (components)
obs('observation-001', 'patient-001', 'James Whitfield', 'encounter-001', labCat(),
  cc(SYS.LOINC, '2339-0', 'Glucose [Mass/volume] in Blood'), q(148, 'mg/dL', 'mg/dL'), '2026-05-04T09:10:00Z');
obs('observation-002', 'patient-001', 'James Whitfield', 'encounter-001', labCat(),
  cc(SYS.LOINC, '4548-4', 'Hemoglobin A1c/Hemoglobin.total in Blood'), q(7.8, '%', '%'), '2026-05-04T09:10:00Z');
obs('observation-003', 'patient-001', 'James Whitfield', 'encounter-001', vitalsCat(),
  cc(SYS.LOINC, '85354-9', 'Blood pressure panel with all children optional'), null, '2026-05-04T09:12:00Z', {
    component: [
      { code: cc(SYS.LOINC, '8480-6', 'Systolic blood pressure'), valueQuantity: q(142, 'mmHg', 'mm[Hg]') },
      { code: cc(SYS.LOINC, '8462-4', 'Diastolic blood pressure'), valueQuantity: q(88, 'mmHg', 'mm[Hg]') },
    ],
  });

// patient-002 hypertension: blood pressure, heart rate
obs('observation-004', 'patient-002', 'Linda Nguyen', null, vitalsCat(),
  cc(SYS.LOINC, '85354-9', 'Blood pressure panel with all children optional'), null, '2026-04-20T14:00:00Z', {
    component: [
      { code: cc(SYS.LOINC, '8480-6', 'Systolic blood pressure'), valueQuantity: q(158, 'mmHg', 'mm[Hg]') },
      { code: cc(SYS.LOINC, '8462-4', 'Diastolic blood pressure'), valueQuantity: q(96, 'mmHg', 'mm[Hg]') },
    ],
  });
obs('observation-005', 'patient-002', 'Linda Nguyen', null, vitalsCat(),
  cc(SYS.LOINC, '8867-4', 'Heart rate'), q(82, '/min', '/min'), '2026-04-20T14:00:00Z');

// patient-003 respiratory: oxygen saturation, respiratory rate
obs('observation-006', 'patient-003', 'Maria Delgado', 'encounter-002', vitalsCat(),
  cc(SYS.LOINC, '2708-6', 'Oxygen saturation in Arterial blood'), q(91, '%', '%'), '2026-05-02T10:30:00Z');
obs('observation-007', 'patient-003', 'Maria Delgado', 'encounter-002', vitalsCat(),
  cc(SYS.LOINC, '9279-1', 'Respiratory rate'), q(24, '/min', '/min'), '2026-05-02T10:30:00Z');

// patient-004 acute: body temperature, heart rate
obs('observation-008', 'patient-004', 'Robert Brooks', 'encounter-003', vitalsCat(),
  cc(SYS.LOINC, '8310-5', 'Body temperature'), q(38.7, 'Cel', 'Cel'), '2026-05-01T22:15:00Z');
obs('observation-009', 'patient-004', 'Robert Brooks', 'encounter-003', vitalsCat(),
  cc(SYS.LOINC, '8867-4', 'Heart rate'), q(104, '/min', '/min'), '2026-05-01T22:15:00Z');

// patient-005 preventive: BMI
obs('observation-010', 'patient-005', 'Grace Chen', null, vitalsCat(),
  cc(SYS.LOINC, '39156-5', 'Body mass index (BMI) [Ratio]'), q(22.4, 'kg/m2', 'kg/m2'), '2026-03-11T08:00:00Z');

// ===========================================================================
// DIAGNOSTIC REPORTS (3) - DiagnosticReport.result[] -> Observation
// ===========================================================================
function diagReport(id, patId, patDisplay, encId, catCode, catDisplay, code, resultIds, issued) {
  return add('DiagnosticReport', id, {
    status: 'final',
    category: [cc('http://terminology.hl7.org/CodeSystem/v2-0074', catCode, catDisplay)],
    code,
    subject: ref('Patient', patId, patDisplay),
    encounter: encId ? ref('Encounter', encId, encId) : undefined,
    effectiveDateTime: issued,
    issued,
    performer: [ref('Organization', 'org-001', 'Segue General Hospital')],
    result: resultIds.map((r) => ref('Observation', r, r)),
  });
}
diagReport('diagnosticreport-001', 'patient-001', 'James Whitfield', 'encounter-001', 'LAB', 'Laboratory',
  cc(SYS.LOINC, '24323-8', 'Comprehensive metabolic 2000 panel - Serum or Plasma'),
  ['observation-001', 'observation-002'], '2026-05-04T11:00:00Z');
diagReport('diagnosticreport-002', 'patient-003', 'Maria Delgado', 'encounter-002', 'RAD', 'Radiology',
  cc(SYS.LOINC, '36643-5', 'Chest X-ray'),
  ['observation-006'], '2026-05-02T12:00:00Z');
diagReport('diagnosticreport-003', 'patient-004', 'Robert Brooks', 'encounter-003', 'LAB', 'Laboratory',
  cc(SYS.LOINC, '58410-2', 'Complete blood count (hemogram) panel - Blood by Automated count'),
  ['observation-008', 'observation-009'], '2026-05-01T23:00:00Z');

// ===========================================================================
// PROCEDURES (3) - Procedure.subject, encounter, performer.actor -> Practitioner
// ===========================================================================
function procedure(id, patId, patDisplay, encId, code, performerId, performerDisplay, performed) {
  return add('Procedure', id, {
    status: 'completed',
    code,
    subject: ref('Patient', patId, patDisplay),
    encounter: ref('Encounter', encId, encId),
    performedDateTime: performed,
    performer: [{ actor: ref('Practitioner', performerId, performerDisplay) }], // nested backbone reference
  });
}
procedure('procedure-001', 'patient-004', 'Robert Brooks', 'encounter-003',
  cc(SYS.CPT, '71046', 'Radiologic examination, chest; 2 views'), 'practitioner-002', 'Dr Daniel Okafor', '2026-05-01T22:40:00Z');
procedure('procedure-002', 'patient-003', 'Maria Delgado', 'encounter-002',
  cc(SYS.CPT, '94010', 'Spirometry'), 'practitioner-003', 'Dr Yuki Tanaka', '2026-05-02T10:45:00Z');
procedure('procedure-003', 'patient-001', 'James Whitfield', 'encounter-001',
  cc(SYS.CPT, '93000', 'Electrocardiogram, complete'), 'practitioner-001', 'Dr Sarah Reynolds', '2026-05-04T09:30:00Z');

// ===========================================================================
// MEDICATION REQUESTS (3) - medicationReference -> Medication, requester -> Practitioner
// ===========================================================================
function medReq(id, patId, patDisplay, encId, medId, medDisplay, requesterId, requesterDisplay, authored, dosageText) {
  const body = {
    status: 'active',
    intent: 'order',
    medicationReference: ref('Medication', medId, medDisplay),
    subject: ref('Patient', patId, patDisplay),
    requester: ref('Practitioner', requesterId, requesterDisplay),
    authoredOn: authored,
    dosageInstruction: [{ text: dosageText }],
  };
  if (encId) body.encounter = ref('Encounter', encId, encId);
  return add('MedicationRequest', id, body);
}
medReq('medicationrequest-001', 'patient-001', 'James Whitfield', 'encounter-001', 'medication-001', 'Metformin 500 MG', 'practitioner-001', 'Dr Sarah Reynolds', '2026-05-04T09:35:00Z', 'Take 1 tablet by mouth twice daily with meals');
medReq('medicationrequest-002', 'patient-002', 'Linda Nguyen', null, 'medication-002', 'Lisinopril 10 MG', 'practitioner-001', 'Dr Sarah Reynolds', '2026-04-20T14:15:00Z', 'Take 1 tablet by mouth once daily');
medReq('medicationrequest-003', 'patient-004', 'Robert Brooks', 'encounter-003', 'medication-003', 'Amoxicillin 500 MG', 'practitioner-002', 'Dr Daniel Okafor', '2026-05-01T23:10:00Z', 'Take 1 capsule by mouth three times daily for 10 days');

// ===========================================================================
// ALLERGY INTOLERANCE (3)
// ===========================================================================
function allergy(id, patId, patDisplay, code, criticality) {
  return add('AllergyIntolerance', id, {
    clinicalStatus: cc('http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical', 'active', 'Active'),
    verificationStatus: cc('http://terminology.hl7.org/CodeSystem/allergyintolerance-verification', 'confirmed', 'Confirmed'),
    type: 'allergy',
    criticality,
    code,
    patient: ref('Patient', patId, patDisplay),
    recordedDate: '2022-01-05',
  });
}
allergy('allergyintolerance-001', 'patient-001', 'James Whitfield', cc(SYS.SNOMED, '91936005', 'Allergy to penicillin'), 'high');
allergy('allergyintolerance-002', 'patient-003', 'Maria Delgado', cc(SYS.SNOMED, '91935009', 'Allergy to peanuts'), 'high');
allergy('allergyintolerance-003', 'patient-004', 'Robert Brooks', cc(SYS.SNOMED, '294530006', 'Allergy to sulfonamide'), 'low');

// ===========================================================================
// CARE PLAN (3) - addresses -> Condition
// ===========================================================================
function carePlan(id, patId, patDisplay, encId, conditionId, title) {
  const body = {
    status: 'active',
    intent: 'plan',
    title,
    subject: ref('Patient', patId, patDisplay),
    addresses: [ref('Condition', conditionId, conditionId)],
    period: { start: '2026-05-04' },
  };
  if (encId) body.encounter = ref('Encounter', encId, encId);
  return add('CarePlan', id, body);
}
carePlan('careplan-001', 'patient-001', 'James Whitfield', 'encounter-001', 'condition-001', 'Type 2 Diabetes management plan');
carePlan('careplan-002', 'patient-002', 'Linda Nguyen', null, 'condition-002', 'Hypertension management plan');
carePlan('careplan-003', 'patient-003', 'Maria Delgado', 'encounter-002', 'condition-003', 'COPD management plan');

// ===========================================================================
// SERVICE REQUEST (3) - subject, encounter, requester
// ===========================================================================
function svcReq(id, patId, patDisplay, encId, code, requesterId, requesterDisplay) {
  return add('ServiceRequest', id, {
    status: 'active',
    intent: 'order',
    code,
    subject: ref('Patient', patId, patDisplay),
    encounter: ref('Encounter', encId, encId),
    requester: ref('Practitioner', requesterId, requesterDisplay),
    authoredOn: '2026-05-04T09:20:00Z',
  });
}
svcReq('servicerequest-001', 'patient-001', 'James Whitfield', 'encounter-001', cc(SYS.LOINC, '4548-4', 'Hemoglobin A1c measurement'), 'practitioner-001', 'Dr Sarah Reynolds');
svcReq('servicerequest-002', 'patient-003', 'Maria Delgado', 'encounter-002', cc(SYS.LOINC, '36643-5', 'Chest X-ray'), 'practitioner-003', 'Dr Yuki Tanaka');
svcReq('servicerequest-003', 'patient-004', 'Robert Brooks', 'encounter-003', cc(SYS.LOINC, '600-7', 'Bacteria identified in Blood by Culture'), 'practitioner-002', 'Dr Daniel Okafor');

// ===========================================================================
// IMMUNIZATION (3) - patient-005 preventive
// ===========================================================================
function immunization(id, patId, patDisplay, cvx, display, date) {
  return add('Immunization', id, {
    status: 'completed',
    vaccineCode: cc(SYS.CVX, cvx, display),
    patient: ref('Patient', patId, patDisplay),
    occurrenceDateTime: date,
    primarySource: true,
  });
}
immunization('immunization-001', 'patient-005', 'Grace Chen', '140', 'Influenza, seasonal, injectable, preservative free', '2025-10-05');
immunization('immunization-002', 'patient-005', 'Grace Chen', '115', 'Tdap', '2024-08-12');
immunization('immunization-003', 'patient-005', 'Grace Chen', '133', 'Pneumococcal conjugate PCV13', '2025-11-01');

// ===========================================================================
// DOCUMENT REFERENCE (3) - DocumentReference.subject, context.encounter
// ===========================================================================
function docRef(id, patId, patDisplay, encId, typeCode, typeDisplay) {
  const body = {
    status: 'current',
    type: cc(SYS.LOINC, typeCode, typeDisplay),
    subject: ref('Patient', patId, patDisplay),
    date: '2026-05-04T12:00:00Z',
    content: [{
      attachment: {
        contentType: 'text/plain',
        // small inline base64 payload ("Clinical note - synthetic test data.")
        data: Buffer.from('Clinical note - synthetic test data.').toString('base64'),
        title: typeDisplay,
      },
    }],
  };
  if (encId) body.context = { encounter: [ref('Encounter', encId, encId)] };
  return add('DocumentReference', id, body);
}
docRef('documentreference-001', 'patient-001', 'James Whitfield', 'encounter-001', '11506-3', 'Progress note');
docRef('documentreference-002', 'patient-004', 'Robert Brooks', 'encounter-003', '18842-5', 'Discharge summary');
docRef('documentreference-003', 'patient-005', 'Grace Chen', null, '34133-9', 'Summary of episode note');

// ===========================================================================
// CARE TEAM (3) - CareTeam.participant.member -> Practitioner
// ===========================================================================
function careTeam(id, patId, patDisplay, encId, memberIds) {
  const body = {
    status: 'active',
    name: `Care team for ${patDisplay}`,
    subject: ref('Patient', patId, patDisplay),
    participant: memberIds.map((m) => ({
      role: [cc(SYS.SNOMED, '158965000', 'Medical practitioner')],
      member: ref('Practitioner', m, m), // nested backbone reference
    })),
  };
  if (encId) body.encounter = ref('Encounter', encId, encId);
  return add('CareTeam', id, body);
}
careTeam('careteam-001', 'patient-001', 'James Whitfield', 'encounter-001', ['practitioner-001', 'practitioner-002']);
careTeam('careteam-002', 'patient-003', 'Maria Delgado', 'encounter-002', ['practitioner-003']);
careTeam('careteam-003', 'patient-004', 'Robert Brooks', 'encounter-003', ['practitioner-002']);

// ===========================================================================
// NEGATIVE / BROKEN-REFERENCE resources (targets deliberately absent)
// ===========================================================================
const NEG = []; // { resourceType, id, resource, references:[], expect }
function neg(resourceType, id, body) {
  const resource = Object.assign(
    { resourceType, id, meta: meta(), identifier: biz(resourceType, id) },
    body
  );
  NEG.push({ resourceType, id, resource });
  return resource;
}
// (1) Observation -> missing Patient
neg('Observation', 'neg-observation-001', {
  status: 'final',
  category: labCat(),
  code: cc(SYS.LOINC, '2339-0', 'Glucose [Mass/volume] in Blood'),
  subject: ref('Patient', 'patient-999-missing', 'MISSING patient'),
  valueQuantity: q(150, 'mg/dL', 'mg/dL'),
  effectiveDateTime: '2026-05-04T09:10:00Z',
});
// (2) Condition -> missing Encounter
neg('Condition', 'neg-condition-001', {
  clinicalStatus: cc('http://terminology.hl7.org/CodeSystem/condition-clinical', 'active', 'Active'),
  code: cc(SYS.SNOMED, '44054006', 'Type 2 diabetes mellitus'),
  subject: ref('Patient', 'patient-001', 'James Whitfield'),
  encounter: ref('Encounter', 'encounter-999-missing', 'MISSING encounter'),
});
// (3) DiagnosticReport -> missing Observation
neg('DiagnosticReport', 'neg-diagnosticreport-001', {
  status: 'final',
  code: cc(SYS.LOINC, '24323-8', 'Comprehensive metabolic panel'),
  subject: ref('Patient', 'patient-001', 'James Whitfield'),
  result: [ref('Observation', 'observation-999-missing', 'MISSING observation')],
});
// (4) MedicationRequest -> missing Medication
neg('MedicationRequest', 'neg-medicationrequest-001', {
  status: 'active',
  intent: 'order',
  medicationReference: ref('Medication', 'medication-999-missing', 'MISSING medication'),
  subject: ref('Patient', 'patient-001', 'James Whitfield'),
});
// (5) Encounter -> missing Practitioner
neg('Encounter', 'neg-encounter-001', {
  status: 'finished',
  class: { system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode', code: 'AMB', display: 'ambulatory' },
  subject: ref('Patient', 'patient-001', 'James Whitfield'),
  participant: [{
    type: [cc('http://terminology.hl7.org/CodeSystem/v3-ParticipationType', 'ATND', 'attender')],
    individual: ref('Practitioner', 'practitioner-999-missing', 'MISSING practitioner'),
  }],
});

// ===========================================================================
// Reference extraction / validation
// ===========================================================================
// Recursively collect every literal reference (objects with a string "reference")
// and every identifier-based logical reference.
function collectRefs(obj, out) {
  if (Array.isArray(obj)) {
    obj.forEach((o) => collectRefs(o, out));
    return;
  }
  if (obj && typeof obj === 'object') {
    if (typeof obj.reference === 'string') {
      out.push({ kind: 'literal', value: obj.reference });
    } else if (obj.identifier && obj.identifier.system && obj.identifier.value && !obj.resourceType) {
      // Heuristic: a Reference datatype carrying only identifier (logical reference).
      // Guard against resource-level .identifier arrays (those are arrays, handled above).
      out.push({ kind: 'logical', value: `${obj.identifier.system}|${obj.identifier.value}` });
    }
    for (const k of Object.keys(obj)) {
      if (k === 'identifier' && Array.isArray(obj[k])) continue; // resource business identifier
      collectRefs(obj[k], out);
    }
  }
}

function tailKey(literal) {
  // "Type/id" or absolute ".../Type/id" -> "Type/id"
  const parts = literal.split('/');
  if (parts.length >= 2) return `${parts[parts.length - 2]}/${parts[parts.length - 1]}`;
  return literal;
}

// ===========================================================================
// File emission
// ===========================================================================
function rimraf(p) { if (fs.existsSync(p)) fs.rmSync(p, { recursive: true, force: true }); }
function ensure(p) { fs.mkdirSync(p, { recursive: true }); }
function writeJson(p, obj) { ensure(path.dirname(p)); fs.writeFileSync(p, JSON.stringify(obj, null, 2) + '\n'); }

// Clean output dirs (keep generate.js and hand-written docs)
rimraf(path.join(ROOT, 'resources'));
rimraf(path.join(ROOT, 'bundles'));
rimraf(path.join(ROOT, 'negative'));

let fileCount = 0;

// --- individual resource files ---
for (const e of registry) {
  const fp = path.join(ROOT, 'resources', e.resourceType, `${e.id}.json`);
  writeJson(fp, e.resource);
  fileCount++;
}

// --- negative resource files ---
for (const n of NEG) {
  const fp = path.join(ROOT, 'negative', `${n.id}.json`);
  writeJson(fp, n.resource);
  fileCount++;
}

// ---------------------------------------------------------------------------
// Dependency closure helper (for self-contained bundles)
// ---------------------------------------------------------------------------
function directDeps(entry) {
  const out = [];
  collectRefs(entry.resource, out);
  const keys = [];
  for (const r of out) {
    if (r.kind !== 'literal') continue;
    const k = tailKey(r.value);
    if (byKey.has(k)) keys.push(k);
  }
  return keys;
}
function closure(startKeys) {
  const seen = new Set();
  const stack = [...startKeys];
  while (stack.length) {
    const k = stack.pop();
    if (seen.has(k)) continue;
    seen.add(k);
    const e = byKey.get(k);
    if (!e) continue;
    for (const dep of directDeps(e)) if (!seen.has(dep)) stack.push(dep);
  }
  return seen;
}

// ---------------------------------------------------------------------------
// HAPI / Aidbox transaction bundles: PUT with deterministic ids.
// ---------------------------------------------------------------------------
function hapiEntry(entry) {
  return {
    fullUrl: `${BASE_URL}/${entry.resourceType}/${entry.id}`,
    resource: entry.resource,
    request: { method: 'PUT', url: `${entry.resourceType}/${entry.id}` },
  };
}
function hapiBundle(entries) {
  return { resourceType: 'Bundle', type: 'transaction', entry: entries.map(hapiEntry) };
}

// foundation: shared resources (no patient-specific clinical data)
const FOUNDATION_TYPES = new Set(['Organization', 'Location', 'Practitioner', 'PractitionerRole', 'Device', 'Medication']);
const foundationEntries = registry.filter((e) => FOUNDATION_TYPES.has(e.resourceType));
writeJson(path.join(ROOT, 'bundles', 'hapi-aidbox', 'foundation-bundle.json'), hapiBundle(foundationEntries));
fileCount++;

// per-patient bundles (patient + everything clinically referencing that patient)
function patientOwnedEntries(patId) {
  const patKey = `Patient/${patId}`;
  return registry.filter((e) => {
    if (FOUNDATION_TYPES.has(e.resourceType)) return false;
    const refs = [];
    collectRefs(e.resource, refs);
    // owned if it references this patient anywhere
    return refs.some((r) => r.kind === 'literal' && tailKey(r.value) === patKey);
  });
}
const patientIds = ['patient-001', 'patient-002', 'patient-003', 'patient-004', 'patient-005'];
for (const pid of patientIds) {
  const patEntry = byKey.get(`Patient/${pid}`);
  const owned = patientOwnedEntries(pid);
  const entries = [patEntry, ...owned];
  writeJson(path.join(ROOT, 'bundles', 'hapi-aidbox', `${pid}-bundle.json`), hapiBundle(entries));
  fileCount++;
}

// realistic "admission" bundle: patient-004 inpatient stay (self-contained via closure)
const admissionSeed = [
  'Patient/patient-004', 'Encounter/encounter-003', 'Condition/condition-004',
  'Observation/observation-008', 'Observation/observation-009',
  'DiagnosticReport/diagnosticreport-003', 'Procedure/procedure-001',
  'MedicationRequest/medicationrequest-003', 'CareTeam/careteam-003',
  'ServiceRequest/servicerequest-003', 'DocumentReference/documentreference-002',
  'RelatedPerson/relatedperson-002', 'Coverage/coverage-003',
];
const admissionKeys = closure(admissionSeed);
// order foundation-ish first for readability (transaction resolves regardless)
const admissionEntries = [...admissionKeys].map((k) => byKey.get(k))
  .sort((a, b) => (FOUNDATION_TYPES.has(b.resourceType) ? 1 : 0) - (FOUNDATION_TYPES.has(a.resourceType) ? 1 : 0));
writeJson(path.join(ROOT, 'bundles', 'hapi-aidbox', 'admission-bundle.json'), hapiBundle(admissionEntries));
fileCount++;

// ---------------------------------------------------------------------------
// Medplum transaction bundles: POST + urn:uuid fullUrls + urn references +
// request.ifNoneExist conditional-create by business identifier.
// Server assigns ids; urn references resolve intra-bundle. Bundles are made
// self-contained (dependency closure) so all urn refs resolve.
// ---------------------------------------------------------------------------
function deterministicUuid(key) {
  // stable UUID derived from the resource key so re-runs are byte-identical
  const h = crypto.createHash('sha1').update('segue-medplum:' + key).digest('hex');
  // format as UUID v4-ish (deterministic; validity of version bits not required for urn:uuid)
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`;
}
function medplumBundle(keys) {
  // assign urn per key
  const urn = new Map();
  for (const k of keys) urn.set(k, `urn:uuid:${deterministicUuid(k)}`);
  const entries = [];
  for (const k of keys) {
    const e = byKey.get(k);
    // deep clone and rewrite references -> urn (only for keys present in bundle)
    const clone = JSON.parse(JSON.stringify(e.resource));
    delete clone.id; // Medplum assigns the logical id
    rewriteRefsToUrn(clone, urn);
    const businessId = clone.identifier && clone.identifier[0];
    const ifNoneExist = businessId ? `identifier=${businessId.system}|${businessId.value}` : undefined;
    entries.push({
      fullUrl: urn.get(k),
      resource: clone,
      request: { method: 'POST', url: e.resourceType, ifNoneExist },
    });
  }
  return { resourceType: 'Bundle', type: 'transaction', entry: entries };
}
function rewriteRefsToUrn(obj, urn) {
  if (Array.isArray(obj)) { obj.forEach((o) => rewriteRefsToUrn(o, urn)); return; }
  if (obj && typeof obj === 'object') {
    if (typeof obj.reference === 'string') {
      const k = tailKey(obj.reference);
      if (urn.has(k)) obj.reference = urn.get(k);
    }
    for (const key of Object.keys(obj)) {
      if (key === 'reference') continue;
      rewriteRefsToUrn(obj[key], urn);
    }
  }
}

// medplum-all: the entire valid graph in one bundle (recommended for Medplum)
const allKeys = registry.map((e) => `${e.resourceType}/${e.id}`);
writeJson(path.join(ROOT, 'bundles', 'medplum', 'medplum-all-bundle.json'), medplumBundle(allKeys));
fileCount++;

// medplum foundation + self-contained per-patient bundles (closure so refs resolve)
const foundationKeys = foundationEntries.map((e) => `${e.resourceType}/${e.id}`);
writeJson(path.join(ROOT, 'bundles', 'medplum', 'foundation-bundle.json'), medplumBundle(foundationKeys));
fileCount++;
for (const pid of patientIds) {
  const owned = patientOwnedEntries(pid).map((e) => `${e.resourceType}/${e.id}`);
  const seed = [`Patient/${pid}`, ...owned];
  const keys = [...closure(seed)];
  writeJson(path.join(ROOT, 'bundles', 'medplum', `${pid}-bundle.json`), medplumBundle(keys));
  fileCount++;
}

// ---------------------------------------------------------------------------
// Negative transaction bundle (HAPI/Aidbox will REJECT; Medplum will ACCEPT)
// ---------------------------------------------------------------------------
const negBundle = {
  resourceType: 'Bundle',
  type: 'transaction',
  entry: NEG.map((n) => ({
    fullUrl: `${BASE_URL}/${n.resourceType}/${n.id}`,
    resource: n.resource,
    request: { method: 'PUT', url: `${n.resourceType}/${n.id}` },
  })),
};
writeJson(path.join(ROOT, 'negative', 'negative-bundle.json'), negBundle);
fileCount++;

// ---------------------------------------------------------------------------
// Manifest + validation report
// ---------------------------------------------------------------------------
const validKeys = new Set(registry.map((e) => `${e.resourceType}/${e.id}`));
const manifest = [];

function manifestRow(resourceType, id, filePath, resource, category) {
  const raw = [];
  collectRefs(resource, raw);
  const references = [];
  const dependencies = new Set();
  for (const r of raw) {
    if (r.kind === 'literal') {
      const key = tailKey(r.value);
      const exists = validKeys.has(key);
      references.push({ style: r.value.startsWith('http') ? 'absolute' : 'relative', reference: r.value, target: key, resolves: exists });
      if (exists) dependencies.add(key);
    } else {
      references.push({ style: 'logical-identifier', reference: r.value, target: null, resolves: 'n/a (resolved by identifier)' });
    }
  }
  return { resourceType, id, filePath: filePath.replace(/\\/g, '/'), testCategory: category, references, dependencies: [...dependencies] };
}

for (const e of registry) {
  const rel = path.relative(ROOT, path.join(ROOT, 'resources', e.resourceType, `${e.id}.json`));
  manifest.push(manifestRow(e.resourceType, e.id, rel, e.resource, e.category));
}
for (const n of NEG) {
  const rel = path.relative(ROOT, path.join(ROOT, 'negative', `${n.id}.json`));
  manifest.push(manifestRow(n.resourceType, n.id, rel, n.resource, 'negative'));
}
writeJson(path.join(ROOT, 'manifest.json'), manifest);
fileCount++;

// counts per resource type
const counts = {};
for (const e of registry) counts[e.resourceType] = (counts[e.resourceType] || 0) + 1;

// --- validation-report.md ---
let totalRefs = 0, resolved = 0, broken = 0, logical = 0;
const lines = [];
lines.push('# Reference Validation Report');
lines.push('');
lines.push(`Generated by \`generate.js\` on ${new Date().toISOString()}.`);
lines.push('');
lines.push('This report is produced from the emitted dataset itself: every literal reference is checked against the set of resources actually present in `resources/`. It reflects reality, not intent.');
lines.push('');
lines.push('> Terminology caveat: SNOMED CT, LOINC, RxNorm, ICD-10-CM, CVX and CPT codes are genuine, commonly used codes but are **NOT** validated against a terminology server here. Only structural correctness and reference resolution are verified.');
lines.push('');
lines.push('## Valid + reference-style resources');
lines.push('');
for (const e of registry) {
  const raw = [];
  collectRefs(e.resource, raw);
  if (raw.length === 0) continue;
  lines.push(`### ${e.resourceType}/${e.id}  _(testCategory: ${e.category})_`);
  for (const r of raw) {
    totalRefs++;
    if (r.kind === 'logical') {
      logical++;
      lines.push(`- logical (identifier) \`${r.value}\` -> resolved by identifier (N/A for literal check)`);
      continue;
    }
    const key = tailKey(r.value);
    const ok = validKeys.has(key);
    if (ok) resolved++; else broken++;
    const style = r.value.startsWith('http') ? 'absolute' : 'relative';
    lines.push(`- ${style} \`${r.value}\` -> ${ok ? 'VALID ✓ (target exists)' : 'INVALID ✗ (target MISSING)'}`);
  }
  lines.push('');
}
lines.push('## Negative resources (intentionally broken; testCategory: negative)');
lines.push('');
lines.push('HAPI + Aidbox will REJECT these on write (referential-integrity, HAPI-1094). Medplum will ACCEPT them (it does not enforce RI). That contrast is the test.');
lines.push('');
let negBroken = 0;
for (const n of NEG) {
  const raw = [];
  collectRefs(n.resource, raw);
  lines.push(`### ${n.resourceType}/${n.id}`);
  for (const r of raw) {
    if (r.kind === 'logical') { lines.push(`- logical \`${r.value}\``); continue; }
    const key = tailKey(r.value);
    const ok = validKeys.has(key);
    if (!ok) negBroken++;
    lines.push(`- \`${r.value}\` -> ${ok ? 'VALID ✓' : 'INVALID ✗ (target MISSING - expected for negative test)'}`);
  }
  lines.push('');
}
lines.push('## Structural sanity summary');
lines.push('');
lines.push(`- Valid/reference-style resources: **${registry.length}**`);
lines.push(`- Negative resources: **${NEG.length}**`);
lines.push(`- Literal references in valid set: **${resolved + broken}** (resolved: **${resolved}**, broken: **${broken}**)`);
lines.push(`- Logical (identifier) references in valid set: **${logical}**`);
lines.push(`- Broken references in negative set: **${negBroken}** (all expected)`);
lines.push('');
lines.push(broken === 0
  ? '**RESULT: All literal references in the valid dataset resolve to a resource present in the dataset. The valid graph has full referential integrity.** ✓'
  : `**RESULT: ${broken} broken reference(s) found in the valid dataset - investigate.** ✗`);
lines.push('');
lines.push('## Counts per resource type (valid set)');
lines.push('');
lines.push('| ResourceType | Count |');
lines.push('|---|---|');
for (const k of Object.keys(counts).sort()) lines.push(`| ${k} | ${counts[k]} |`);
lines.push(`| **TOTAL** | **${registry.length}** |`);
lines.push('');
fs.writeFileSync(path.join(ROOT, 'validation-report.md'), lines.join('\n'));
fileCount++;

// ---------------------------------------------------------------------------
// Console summary
// ---------------------------------------------------------------------------
console.log('FHIR R4 test dataset generated.');
console.log('Valid resources:', registry.length, '| Negative resources:', NEG.length);
console.log('Counts per type:', JSON.stringify(counts));
console.log('Literal refs resolved:', resolved, '| broken (valid set):', broken, '| logical:', logical);
console.log('Broken refs in negative set:', negBroken, '(expected', NEG.length, ')');
console.log('Total files written:', fileCount);
if (broken !== 0) { console.error('ERROR: valid dataset has broken references!'); process.exit(1); }
