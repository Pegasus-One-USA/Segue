import { buildMappingSummaryDocument, applyMappingSummaryDocument, MappingSummaryDocument } from './field-mapping-summary.model';
import { MappingRow } from './field-mapping-model';
import { ResourceFieldDef } from '../destination-wizard.component';

const PATIENT_FIELDS: ResourceFieldDef[] = [
  { label: 'Id', path: 'Patient.id', sqlColumn: 'Id', csvColumn: 'Id', jsonPath: '$.id', valueType: 'String' },
  { label: 'Family', path: 'Patient.name.family', sqlColumn: 'Family', csvColumn: 'Family', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
  { label: 'Given', path: 'Patient.name.given', sqlColumn: 'Given', csvColumn: 'Given', jsonPath: '$.name[*].given', valueType: 'String', arrays: ['name'] },
];

/**
 * The "MPI Rule" tab's choice (MappingRow.isMpiMatch) must survive reopening the destination: a reopened node is
 * rebuilt from the Mapping JSON summary, not from dest_mappings_v2, so a flag the summary drops is a choice the
 * user silently loses on the next edit — the same trap jsonWriteMode fell into.
 */
describe('isMpiMatch round-trip through the Mapping JSON summary', () => {
  function doc(rows: MappingRow[]): MappingSummaryDocument {
    return buildMappingSummaryDocument({
      sourceVendor: 'EPIC', destType: 'mongo', destLabel: 'MongoDB',
      mappingRows: rows, sqlTables: [], childTableRelationsByTable: {},
      availableFields: (r: string) => (r === 'Patient' ? PATIENT_FIELDS : []),
      sourceConnectionId: null, destinationId: null,
      targetByResource: { Patient: 'patients' },
    });
  }
  const columns = (d: MappingSummaryDocument) => d.mappings[0].tables[0].columns;
  const restored = (rows: MappingRow[]) => applyMappingSummaryDocument(doc(rows), 'mongo').mappingRows;

  const direct = (targetName: string, fhirPath: string, isMpiMatch?: boolean): MappingRow => ({
    resource: 'Patient', sources: [{ fhirPath, label: fhirPath }], mode: 'value', instance: { type: 'first' },
    targetName, tableName: 'patients', ...(isMpiMatch ? { isMpiMatch } : {}),
  });

  it('records and restores the choice on a direct field', () => {
    expect(columns(doc([direct('Id', 'Patient.id', true)]))[0].isMpiMatch).toBeTrue();
    expect(restored([direct('Id', 'Patient.id', true)])[0].isMpiMatch).toBeTrue();
  });

  it('records and restores it on a joined field', () => {
    const join: MappingRow = {
      resource: 'Patient', mode: 'value', instance: { type: 'first' }, delimiter: ' ',
      sources: [{ fhirPath: 'Patient.name.given', label: 'Given' }, { fhirPath: 'Patient.name.family', label: 'Family' }],
      targetName: 'FullName', tableName: 'patients', isMpiMatch: true,
    };
    expect(columns(doc([join]))[0].isMpiMatch).toBeTrue();
    expect(restored([join])[0].isMpiMatch).toBeTrue();
  });

  it('records and restores it on a whole-node field', () => {
    const whole: MappingRow = {
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.name',
      targetName: 'Names', tableName: 'patients', isMpiMatch: true,
    };
    expect(restored([whole])[0].isMpiMatch).toBeTrue();
  });

  it('writes no key on a field nobody chose, so existing documents stay as they were', () => {
    expect('isMpiMatch' in columns(doc([direct('Id', 'Patient.id')]))[0]).toBeFalse();
    expect('isMpiMatch' in restored([direct('Id', 'Patient.id')])[0]).toBeFalse();
  });

  it('drops a stray flag on a fixed-value column, which has no source to match on', () => {
    const fixed: MappingRow = {
      resource: 'Patient', sources: [], mode: 'default', defaultToken: '@default', defaultValue: 'x',
      targetName: 'Origin', tableName: 'patients', isMpiMatch: true,
    };
    expect('isMpiMatch' in columns(doc([fixed]))[0]).toBeFalse();
  });
});
