import { EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';

/** Spec-only: the write capabilities each vendor reports, as EhrWriteCapabilities.cs lists them (the fields the
 *  kinds and the grid read). */
export function cap(resourceType: string, extra: Partial<EhrWriteCapability> = {}): EhrWriteCapability {
  return {
    resourceType,
    operations: ['Create'],
    vendorApiId: `${resourceType.toLowerCase()}-create`,
    variant: null,
    requiresEncounter: false,
    optInOnly: false,
    allowedApplicationTypes: ['Backend'],
    liveWriteSupported: true,
    ...extra,
  };
}

const optIn = (variant: string, extra: Partial<EhrWriteCapability> = {}): Partial<EhrWriteCapability> =>
  ({ variant, optInOnly: true, requiresVariantOptIn: true, ...extra });

export const EPIC_CAPABILITIES: EhrWriteCapability[] = [
  cap('AllergyIntolerance', { vendorApiId: '945' }),
  cap('BodyStructure', { vendorApiId: '11040', ...optIn('radiotherapy-volume') }),
  cap('Communication', { vendorApiId: '10090', ...optIn('community-resource-message', { requiresTargetReferences: true }) }),
  cap('Condition', { vendorApiId: '949', variant: 'problem-list-item' }),
  cap('DocumentReference', { vendorApiId: '1046', variant: 'clinical-note' }),
  cap('DocumentReference', { vendorApiId: '10050', ...optIn('document-information') }),
  cap('DocumentReference', { vendorApiId: '10303', ...optIn('non-patient-document', { requiresTargetReferences: true }) }),
  cap('Observation', { vendorApiId: '963', variant: 'vital-signs' }),
  cap('Observation', { vendorApiId: '962', ...optIn('lines-drains-airways') }),
  cap('Observation', { vendorApiId: '11224', ...optIn('dicom-image-characteristics', { requiresTargetReferences: true }) }),
  cap('Patient', { vendorApiId: '930', optInOnly: true }),
  cap('Procedure', { vendorApiId: '11048', ...optIn('external-radiotherapy-summary') }),
  cap('QuestionnaireResponse', { vendorApiId: '10023', ...optIn('patient-entered-questionnaire', { requiresTargetReferences: true }) }),
  cap('ServiceRequest', { vendorApiId: '11044', ...optIn('external-radiotherapy-summary') }),
];

const ecw = (resourceType: string, extra: Partial<EhrWriteCapability> = {}) =>
  cap(resourceType, { requiresVendorActivation: true, ...extra });

export const ECW_CAPABILITIES: EhrWriteCapability[] = [
  ecw('AllergyIntolerance'),
  ecw('Condition', { vendorApiId: 'ecw-condition-problems-create', variant: 'problem-list-item' }),
  ecw('Condition', { vendorApiId: 'ecw-condition-encounter-diagnosis-create', variant: 'encounter-diagnosis' }),
  ecw('Condition', { vendorApiId: 'ecw-condition-medical-history-create', variant: 'medical-history', optInOnly: true, createsHolderEncounter: true }),
  ecw('DocumentReference', { variant: 'clinical-note' }),
  ecw('Immunization', { variant: 'historical-immunization' }),
  ecw('MedicationRequest', { variant: 'medication-list' }),
  ecw('MedicationStatement', { variant: 'medication-list' }),
  ecw('Observation', { variant: 'vital-signs' }),
  ecw('Patient', { optInOnly: true }),
  ecw('Procedure', { vendorApiId: 'ecw-procedure-surgical-history-create', variant: 'surgical-history', optInOnly: true, createsHolderEncounter: true }),
  cap('QuestionnaireResponse', { liveWriteSupported: false }),
];

const athena = (resourceType: string, extra: Partial<EhrWriteCapability> = {}) =>
  cap(resourceType, { requiresVendorActivation: true, ...extra });

export const ATHENA_CAPABILITIES: EhrWriteCapability[] = [
  athena('AllergyIntolerance'),
  athena('Condition', { variant: 'problem-list-item' }),
  athena('DocumentReference', { variant: 'clinical-note' }),
  athena('Observation', { vendorApiId: 'athenaone-encounter-vitals-post', variant: 'vital-signs' }),
  athena('Observation', { vendorApiId: 'athenaone-documents-labresult-post', variant: 'laboratory-result' }),
  athena('Patient', { optInOnly: true }),
  cap('QuestionnaireResponse', { liveWriteSupported: false }),
];

export const GENERIC_FHIR_CAPABILITIES: EhrWriteCapability[] = [
  'AllergyIntolerance', 'BodyStructure', 'Communication', 'Condition', 'DocumentReference', 'Observation', 'Patient',
  'Procedure', 'QuestionnaireResponse', 'ServiceRequest',
].map((type) => cap(type, { vendorApiId: 'fhir-r4-create', optInOnly: type === 'Patient' }));
