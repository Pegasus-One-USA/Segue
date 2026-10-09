import { SOURCES } from '../data/sources.data';
import { TRANSFORMS } from '../data/transforms.data';
import { EHR_VENDOR_TO_SOURCE_FORM_KEY } from '../components/node-library/source-form.registry';
import { WRITE_VENDOR_LABELS, isEhrWriteVendor } from './ehr-write-vendors';

/** What admins call the write vendors (eClinicalWorks, not Healow); the SOURCES catalog names cover the rest. On the
 *  read side a plain FHIR server keeps its catalog name ("Generic FHIR R4"). */
const EHR_LABELS: Record<string, string> = {
  Epic: WRITE_VENDOR_LABELS.Epic,
  Healow: WRITE_VENDOR_LABELS.Healow,
  Athenahealth: WRITE_VENDOR_LABELS.Athenahealth,
};

/** The Type column for an EHR read connection, e.g. "eClinicalWorks" or "Generic FHIR R4". */
export function ehrVendorLabel(vendor: string): string {
  if (EHR_LABELS[vendor]) return EHR_LABELS[vendor];
  const sourceId = EHR_VENDOR_TO_SOURCE_FORM_KEY[vendor];
  return SOURCES.find(s => s.id === sourceId)?.name ?? vendor;
}

/** The vendor part of an EHR write connection's label: a plain FHIR server is "FHIR server" here. */
export function writeVendorLabel(vendor: string): string {
  return isEhrWriteVendor(vendor) ? WRITE_VENDOR_LABELS[vendor] : ehrVendorLabel(vendor);
}

const ENGINE_LABELS: Record<string, string> = {
  sqlserver: 'SQL Server',
  postgresql: 'PostgreSQL',
  mysql: 'MySQL',
};

export function databaseTypeLabel(engine: string): string {
  return `SQL database (${ENGINE_LABELS[engine] ?? engine})`;
}

/** The v1 catalog the pages use has no EhrWriteBack entry (only transforms-v2 does). */
const DESTINATION_LABEL_OVERRIDES: Record<string, string> = {
  EhrWriteBack: 'EHR write-back',
};

export function destinationTypeLabel(type: string): string {
  return DESTINATION_LABEL_OVERRIDES[type]
    ?? TRANSFORMS.find(t => t.destinationType === type)?.name
    ?? type;
}

/** The SMART application type an EHR read connection is set up under — the "Audience" column. */
export const AUDIENCE_LABELS: Readonly<Record<string, string>> = {
  Backend: 'Backend System',
  EhrLaunch: 'Provider EHR Launch',
  Standalone: 'Provider Standalone',
  Patient: 'Patient',
};

export function audienceLabel(applicationType: string | null | undefined): string | null {
  return (applicationType && AUDIENCE_LABELS[applicationType]) || null;
}
