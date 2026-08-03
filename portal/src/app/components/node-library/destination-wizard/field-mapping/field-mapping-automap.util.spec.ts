import { autoMap } from './field-mapping-automap.util';
import { buildForest } from './field-mapping-tree.util';
import { ResourceFieldDef } from '../destination-wizard.component';
import { MappingRow } from './field-mapping-model';

const FIELDS: ResourceFieldDef[] = [
  { label: 'First Name', path: 'Patient.name.given', sqlColumn: 'FirstName', csvColumn: 'FirstName', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
  { label: 'Last Name', path: 'Patient.name.family', sqlColumn: 'LastName', csvColumn: 'LastName', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
  { label: 'Date of Birth', path: 'Patient.birthDate', sqlColumn: 'DateOfBirth', csvColumn: 'DateOfBirth', jsonPath: '$.birthDate', valueType: 'Date', arrays: [] },
];

function forest() {
  return buildForest(['Patient'], () => FIELDS);
}

describe('autoMap', () => {
  it('matches an exact normalized label', () => {
    const added = autoMap(forest(), [], {}, () => ['FirstName']);
    expect(added.length).toBe(1);
    expect(added[0].sources[0].fhirPath).toBe('Patient.name.given');
    expect(added[0].targetName).toBe('FirstName');
    expect(added[0].instance).toEqual({ type: 'all' });
  });

  it('matches snake_case vs PascalCase via normalization', () => {
    const added = autoMap(forest(), [], {}, () => ['date_of_birth']);
    expect(added.length).toBe(1);
    expect(added[0].sources[0].fhirPath).toBe('Patient.birthDate');
  });

  it('falls back to substring containment', () => {
    const added = autoMap(forest(), [], {}, () => ['PatientFirstNameColumn']);
    expect(added.length).toBe(1);
    expect(added[0].sources[0].fhirPath).toBe('Patient.name.given');
  });

  it('skips columns with no plausible match', () => {
    const added = autoMap(forest(), [], {}, () => ['CompletelyUnrelatedThing']);
    expect(added.length).toBe(0);
  });

  it('skips columns that already have a mapping', () => {
    const existing: MappingRow[] = [{
      resource: 'Patient',
      sources: [{ fhirPath: 'Patient.name.given', label: 'First Name' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'FirstName', tableName: 'dbo.Patient',
    }];
    const added = autoMap(forest(), existing, {}, () => ['FirstName', 'LastName']);
    expect(added.map(a => a.targetName)).toEqual(['LastName']);
  });
});
