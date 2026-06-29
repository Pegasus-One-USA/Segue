import { Source } from '../models/source.model';

export const SOURCES: Source[] = [
  { id: 'epic',         abbr: 'EP',  color: '#ff5a4f', context: 'Backend system',        name: 'Epic',                    sub: 'Epic on FHIR — full connection wizard.' },
  { id: 'cerner',       abbr: 'OH',  color: '#1175bb', context: 'Backend system',        name: 'Cerner (Oracle Health)',  sub: 'Oracle Health Millennium FHIR R4.' },
  { id: 'athena',       abbr: 'ATH', color: '#7b2ff7', context: 'Provider (standalone)', name: 'Athenahealth',            sub: 'athenahealth FHIR R4 APIs.' },
  { id: 'allscripts',   abbr: 'ALS', color: '#0aa1a1', context: 'Provider (standalone)', name: 'Allscripts (Veradigm)',   sub: 'Veradigm / Allscripts FHIR R4.' },
  { id: 'healow',       abbr: 'HEL', color: '#e8590c', context: 'Patient (standalone)',  name: 'Healow (eClinicalWorks)', sub: 'eClinicalWorks Healow FHIR R4.' },
  { id: 'meditech',     abbr: 'MED', color: '#2f6f4f', context: 'Backend system',        name: 'Meditech Greenfield',     sub: 'Meditech Greenfield FHIR R4.' },
  { id: 'generic-fhir', abbr: 'R4',  color: '#5b6573', context: 'Backend system',        name: 'Generic FHIR R4',         sub: 'Any conformant FHIR R4 server.' },
  { id: 'hl7v2',        abbr: 'HL7', color: '#b45309', context: 'Backend system',        name: 'HL7 v2 / MLLP',           sub: 'HL7 v2 over MLLP (ingest + map).' },
  { id: 'sample',       abbr: 'SMP', color: '#8b8f98', context: 'Backend system',        name: 'Sample (sandbox)',        sub: 'Synthetic sample data for testing.' },
];
