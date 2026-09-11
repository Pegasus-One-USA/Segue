import { Source } from '../models/source.model';

// permissionPrefix = the vendor's PermissionGroupCode name (see permission.constants.ts). Populating it
// activates the per-vendor RBAC that node-library-dialog (tile visibility = `{prefix}.view`), openSourceForm
// (add = `{prefix}.create`, edit = `{prefix}.edit`) and the canvas delete gate (`{prefix}.delete`) were all
// already written against — without it those checks silently no-op and every source is ungated. HL7 v2 has no
// dedicated permission group, so it is intentionally left without a prefix (falls back to sourceconnections.*).
export const SOURCES: Source[] = [
  { id: 'epic',         abbr: 'EP',  color: '#ff5a4f', context: 'Backend system',        name: 'Epic',                    sub: 'Epic on FHIR — full connection wizard.',  permissionPrefix: 'epic' },
  { id: 'cerner',       abbr: 'OH',  color: '#1175bb', context: 'Backend system',        name: 'Cerner (Oracle Health)',  sub: 'Oracle Health Millennium FHIR R4.',       permissionPrefix: 'cerner' },
  { id: 'athena',       abbr: 'ATH', color: '#7b2ff7', context: 'Provider (standalone)', name: 'Athenahealth',            sub: 'athenahealth FHIR R4 APIs.',              permissionPrefix: 'athenahealth' },
  { id: 'allscripts',   abbr: 'ALS', color: '#0aa1a1', context: 'Provider (standalone)', name: 'Allscripts (Veradigm)',   sub: 'Veradigm / Allscripts FHIR R4.',          permissionPrefix: 'allscripts' },
  { id: 'healow',       abbr: 'ECW', color: '#e8590c', context: 'Patient (standalone)',  name: 'eClinicalWorks',          sub: 'eClinicalWorks (Healow) FHIR R4.',        permissionPrefix: 'healow' },
  { id: 'meditech',     abbr: 'MED', color: '#2f6f4f', context: 'Backend system',        name: 'Meditech Greenfield',     sub: 'Meditech Greenfield FHIR R4.',            permissionPrefix: 'meditechgreenfield' },
  { id: 'generic-fhir', abbr: 'R4',  color: '#5b6573', context: 'Backend system',        name: 'Generic FHIR R4',         sub: 'Any conformant FHIR R4 server.',          permissionPrefix: 'genericfhir' },
  { id: 'hl7v2',        abbr: 'HL7', color: '#b45309', context: 'Backend system',        name: 'HL7 v2 / MLLP',           sub: 'HL7 v2 over MLLP (ingest + map).' },
  { id: 'sample',       abbr: 'SMP', color: '#8b8f98', context: 'Backend system',        name: 'Sample (sandbox)',        sub: 'Synthetic sample data for testing.',      permissionPrefix: 'sample' },
];
