import {
  migrateLegacyRow, serializeRowsFlat, resolveArrayPolicy, isApproximated,
  MappingRow, LegacyMappingRow,
} from './field-mapping-model';

describe('migrateLegacyRow', () => {
  it('defaults instance to {type:"first"} — not "all" — to preserve current buildMapping() behavior', () => {
    const legacy: LegacyMappingRow = {
      resource: 'Patient', field: 'First Name', path: 'Patient.name.given',
      target: 'dbo.Patient', column: 'FirstName', jsonPath: '$.name[*].given[*]',
      valueType: 'String', arrays: ['name'],
    };
    const migrated = migrateLegacyRow(legacy);
    expect(migrated.instance).toEqual({ type: 'first' });
    expect(migrated.mode).toBe('value');
    expect(migrated.sources).toEqual([{
      fhirPath: 'Patient.name.given', label: 'First Name',
      jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'],
    }]);
    expect(migrated.targetName).toBe('FirstName');
    expect(migrated.tableName).toBe('dbo.Patient');
  });
});

describe('serializeRowsFlat', () => {
  it('emits one legacy-shaped entry per row using the primary (first) source', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient',
      sources: [
        { fhirPath: 'Patient.name.given', label: 'First Name', jsonPath: '$.name[*].given[*]', valueType: 'String', arrays: ['name'] },
        { fhirPath: 'Patient.name.family', label: 'Last Name', jsonPath: '$.name[*].family', valueType: 'String', arrays: ['name'] },
      ],
      mode: 'value',
      delimiter: ' ',
      instance: { type: 'first' },
      targetName: 'FullName',
      tableName: 'dbo.Patient',
    }];
    const flat = serializeRowsFlat(rows, { Patient: 'dbo.Patient' });
    expect(flat.length).toBe(1);
    expect(flat[0].field).toBe('First Name');
    expect(flat[0].path).toBe('Patient.name.given');
    expect(flat[0].column).toBe('FullName');
    expect(flat[0].target).toBe('dbo.Patient');
    expect(flat[0].approximated).toBeTrue(); // join has no backend representation
  });

  it('sets valueType to Json and arrayPolicy to StoreJson for childJson rows', () => {
    const rows: MappingRow[] = [{
      resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'name',
      targetName: 'NameJson', tableName: 'dbo.Patient',
    }];
    const flat = serializeRowsFlat(rows, {});
    expect(flat[0].arrayPolicy).toBe('StoreJson');
    expect(flat[0].valueType).toBe('Json');
    expect(flat[0].approximated).toBeFalse();
  });
});

describe('resolveArrayPolicy', () => {
  const baseSource = { fhirPath: 'Patient.name.given', label: 'First Name' };
  const arraySource = { ...baseSource, arrays: ['name'] };
  const scalarSource = { ...baseSource, arrays: [] };

  function row(overrides: Partial<MappingRow>): MappingRow {
    return {
      resource: 'Patient', sources: [arraySource], mode: 'value',
      targetName: 'FirstName', tableName: 'dbo.Patient', ...overrides,
    };
  }

  it('childJson mode -> StoreJson, not approximated', () => {
    expect(resolveArrayPolicy(row({ mode: 'childJson', sources: [], childNodeId: 'name' })))
      .toEqual({ arrayPolicy: 'StoreJson', approximated: false });
  });

  it('single source, instance "first" -> FirstItem, not approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'first' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false });
  });

  it('single source, instance "all"/aggregate rows, has array ancestors -> RepeatParent', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'all', aggregate: 'rows' } })))
      .toEqual({ arrayPolicy: 'RepeatParent', approximated: false });
  });

  it('single source, instance "all", no array ancestors -> Scalar', () => {
    expect(resolveArrayPolicy(row({ sources: [scalarSource], instance: { type: 'all', aggregate: 'rows' } })))
      .toEqual({ arrayPolicy: 'Scalar', approximated: false });
  });

  it('single source, instance "all"/aggregate csv -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'all', aggregate: 'csv' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('single source, instance "nth" -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'nth', n: 2 } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('single source, instance "criteria" -> FirstItem, approximated', () => {
    expect(resolveArrayPolicy(row({ instance: { type: 'criteria', field: 'use', op: '=', value: 'official' } })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('joined sources (>1) -> rules applied to sources[0], always approximated', () => {
    const joined = row({
      sources: [arraySource, { ...baseSource, fhirPath: 'Patient.name.family', label: 'Last Name' }],
      instance: { type: 'first' },
    });
    expect(resolveArrayPolicy(joined)).toEqual({ arrayPolicy: 'FirstItem', approximated: true });
  });

  it('absent instance defaults to "first" semantics', () => {
    expect(resolveArrayPolicy(row({ instance: undefined })))
      .toEqual({ arrayPolicy: 'FirstItem', approximated: false });
  });
});

describe('isApproximated', () => {
  it('mirrors resolveArrayPolicy().approximated', () => {
    const exact: MappingRow = {
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Patient ID' }],
      mode: 'value', instance: { type: 'first' }, targetName: 'SourcePatientId', tableName: 'dbo.Patient',
    };
    const approx: MappingRow = { ...exact, instance: { type: 'nth', n: 1 } };
    expect(isApproximated(exact)).toBeFalse();
    expect(isApproximated(approx)).toBeTrue();
  });
});
